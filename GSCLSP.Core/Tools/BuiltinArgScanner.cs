using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Globalization;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Tools;

public static partial class BuiltinArgScanner
{
    private static readonly Regex CallRegex = BuiltinCallRegex();
    private static readonly Regex GscToolMapRegex = GscToolRegex();
    private const string GscToolRawBaseUrl = "https://raw.githubusercontent.com/xensik/gsc-tool/refs/heads/dev/src/gsc/engine";

    public static Task InferArgsAsync(string dumpFolder, string builtinsJsonPath, CancellationToken ct = default) =>
        InferArgsAsync(dumpFolder, builtinsJsonPath, null, null, false, ct);

    public static async Task InferArgsAsync(
        string dumpFolder,
        string builtinsJsonPath,
        string? matchesLogPath,
        string? matchNameFilter = null,
        bool includeArgsArray = false,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(dumpFolder))
            throw new DirectoryNotFoundException(dumpFolder);

        if (!File.Exists(builtinsJsonPath))
            throw new FileNotFoundException("Missing builtins json.", builtinsJsonPath);

        JsonNode rootNode = JsonNode.Parse(await File.ReadAllTextAsync(builtinsJsonPath, ct))
                            ?? throw new InvalidOperationException("Invalid JSON.");

        JsonObject root = rootNode.AsObject();
        JsonArray functions = root["functions"] as JsonArray ?? [];
        JsonArray methods = root["methods"] as JsonArray ?? [];
        var game = root["game"]?.GetValue<string>();
        var aliasMap = await TryLoadGameAliasesAsync(game, ct);
        var functionAliases = MergeAliases(aliasMap?.TokensById, aliasMap?.FunctionsById);

        var functionStats = new Dictionary<string, BuiltinStats>(StringComparer.OrdinalIgnoreCase);
        var methodStats = new Dictionary<string, BuiltinStats>(StringComparer.OrdinalIgnoreCase);
        var observations = string.IsNullOrWhiteSpace(matchesLogPath) ? null : new List<CallObservation>();

        Register(functions, "function", functionStats, functionAliases);
        Register(methods, "method", methodStats, aliasMap?.MethodsById);

        foreach (var file in Directory.EnumerateFiles(dumpFolder, "*.gsc", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string text = await File.ReadAllTextAsync(file, ct);
            ScanText(file, text, functionStats, methodStats, observations, matchNameFilter);
        }

        Apply(functions, functionStats, includeArgsArray, functionAliases);
        Apply(methods, methodStats, includeArgsArray, aliasMap?.MethodsById);

        var options = new JsonSerializerOptions { WriteIndented = true };
        Console.WriteLine("Writing results to " + builtinsJsonPath);
        await File.WriteAllTextAsync(builtinsJsonPath, root.ToJsonString(options), ct);

        if (!string.IsNullOrWhiteSpace(matchesLogPath))
        {
            var dir = Path.GetDirectoryName(matchesLogPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var sorted = (observations ?? [])
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Line)
                .ToList();

            await File.WriteAllTextAsync(matchesLogPath, JsonSerializer.Serialize(sorted, options), ct);
        }
    }

    private static void Register(
        JsonArray arr,
        string kind,
        Dictionary<string, BuiltinStats> stats,
        IReadOnlyDictionary<string, string>? aliasesById = null)
    {
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;
            string? name = obj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!stats.ContainsKey(name))
                stats[name] = new BuiltinStats(name, kind);

            if (aliasesById == null)
                continue;

            if (!aliasesById.TryGetValue(name, out var alias) || string.IsNullOrWhiteSpace(alias))
                continue;

            if (!stats.ContainsKey(alias))
                stats[alias] = stats[name];
        }
    }

    private static async Task<GameAliasMap?> TryLoadGameAliasesAsync(string? game, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(game))
            return null;

        var gameKey = game.Trim().ToLowerInvariant();

        try
        {
            using var http = new HttpClient();
            var funcTask = TryGetFirstAvailableSourceAsync(http, ct,
                $"{GscToolRawBaseUrl}/{gameKey}_func.cpp",
                $"{GscToolRawBaseUrl}/{gameKey}_pc_func.cpp");

            var methTask = TryGetFirstAvailableSourceAsync(http, ct,
                $"{GscToolRawBaseUrl}/{gameKey}_meth.cpp",
                $"{GscToolRawBaseUrl}/{gameKey}_pc_meth.cpp");

            var tokenTask = TryGetFirstAvailableSourceAsync(http, ct,
                $"{GscToolRawBaseUrl}/{gameKey}_token.cpp",
                $"{GscToolRawBaseUrl}/{gameKey}_pc_token.cpp");

            await Task.WhenAll(funcTask, methTask, tokenTask);

            var funcSource = await funcTask;
            var methSource = await methTask;
            var tokenSource = await tokenTask;

            if (funcSource == null || methSource == null || tokenSource == null)
                return null;

            return new GameAliasMap(
                ParseGscToolMap(funcSource),
                ParseGscToolMap(methSource),
                ParseGscToolMap(tokenSource));
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetFirstAvailableSourceAsync(HttpClient http, CancellationToken ct, params string[] urls)
    {
        foreach (var url in urls)
        {
            try
            {
                return await http.GetStringAsync(url, ct);
            }
            catch
            {
                // try next naming convention
            }
        }

        return null;
    }

    private static Dictionary<string, string>? MergeAliases(
        IReadOnlyDictionary<string, string>? fallback,
        IReadOnlyDictionary<string, string>? primary)
    {
        if (fallback == null && primary == null)
            return null;

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (fallback != null)
        {
            foreach (var pair in fallback)
                merged[pair.Key] = pair.Value;
        }

        if (primary != null)
        {
            foreach (var pair in primary)
                merged[pair.Key] = pair.Value;
        }

        return merged;
    }

    private static Dictionary<string, string> ParseGscToolMap(string source)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in GscToolMapRegex.Matches(source))
        {
            if (!match.Success)
                continue;

            var idHex = match.Groups["id"].Value;
            var name = match.Groups["name"].Value;
            if (string.IsNullOrWhiteSpace(idHex) || string.IsNullOrWhiteSpace(name))
                continue;

            if (!ulong.TryParse(idHex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
                continue;

            result[value.ToString(CultureInfo.InvariantCulture)] = name;
        }

        return result;
    }

    private static void ScanText(
        string filePath,
        string text,
        Dictionary<string, BuiltinStats> functionStats,
        Dictionary<string, BuiltinStats> methodStats,
        List<CallObservation>? observations,
        string? matchNameFilter)
    {
        foreach (Match m in CallRegex.Matches(text))
        {
            string rawName = m.Groups["name"].Value;
            var (normalizedName, forcedKind) = NormalizeCallName(rawName);

            BuiltinStats? functionMatch = null;
            BuiltinStats? methodMatch = null;

            if (forcedKind == "function")
            {
                functionStats.TryGetValue(normalizedName, out functionMatch);
                if (functionMatch == null)
                    methodStats.TryGetValue(normalizedName, out methodMatch);
            }
            else if (forcedKind == "method")
            {
                methodStats.TryGetValue(normalizedName, out methodMatch);
                if (methodMatch == null)
                    functionStats.TryGetValue(normalizedName, out functionMatch);
            }
            else
            {
                functionStats.TryGetValue(normalizedName, out functionMatch);
                methodStats.TryGetValue(normalizedName, out methodMatch);
            }

            if (functionMatch == null && methodMatch == null)
                continue;

            int openParen = m.Index + m.Length - 1;
            int closeParen = FindMatchingParen(text, openParen);
            if (closeParen < 0) continue;

            string inside = text[(openParen + 1)..closeParen];
            int argc = CountTopLevelArgs(inside);

            functionMatch?.Observe(argc);
            methodMatch?.Observe(argc);

            if (observations is not null &&
                (string.IsNullOrWhiteSpace(matchNameFilter) || normalizedName.Equals(matchNameFilter, StringComparison.OrdinalIgnoreCase)))
            {
                var (line, column) = GetLineAndColumn(text, m.Index);
                observations.Add(new CallObservation(
                    Name: normalizedName,
                    ArgCount: argc,
                    FilePath: filePath,
                    Line: line,
                    Column: column,
                    Snippet: ExtractSnippet(text, m.Index),
                    ArgsText: inside.Trim()));
            }
        }
    }

    private static (string Name, string? Kind) NormalizeCallName(string rawName)
    {
        string name = rawName;

        if (rawName.StartsWith("_id_", StringComparison.OrdinalIgnoreCase))
            return (NormalizeBuiltinName(rawName[4..]), "function");

        if (rawName.StartsWith("_meth_", StringComparison.OrdinalIgnoreCase))
            return (NormalizeBuiltinName(rawName[6..]), "method");

        return (NormalizeBuiltinName(name), null);
    }

    private static string NormalizeBuiltinName(string name)
    {
        if (name.StartsWith("0x", StringComparison.OrdinalIgnoreCase) &&
            ulong.TryParse(name[2..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var value))
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }

        return name;
    }

    private static void Apply(
        JsonArray arr,
        Dictionary<string, BuiltinStats> stats,
        bool includeArgsArray,
        IReadOnlyDictionary<string, string>? aliasesById = null)
    {
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;
            string? name = obj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            var lookupName = name;
            if (!stats.TryGetValue(lookupName, out var s) || s.ObservedCalls == 0) continue;

            if (aliasesById != null && aliasesById.TryGetValue(name, out var resolvedName) && !string.IsNullOrWhiteSpace(resolvedName))
            {
                obj["name"] = resolvedName;
            }

            obj["minArgs"] = s.MinArgs;
            obj["maxArgs"] = s.MaxArgs;
            //obj["observedCalls"] = s.ObservedCalls;

            if (!includeArgsArray)
            {
                obj.Remove("args");
                continue;
            }

            var args = new JsonArray();
            for (int i = 0; i < s.MaxArgs; i++)
                args.Add($"arg{i}");
            obj["args"] = args;
        }
    }

    private static int FindMatchingParen(string text, int openParenIndex)
    {
        int depth = 0;
        bool inString = false;

        for (int i = openParenIndex; i < text.Length; i++)
        {
            char c = text[i];

            if (c == '"' && (i == 0 || text[i - 1] != '\\'))
                inString = !inString;

            if (inString) continue;

            if (c == '(') depth++;
            else if (c == ')')
            {
                depth--;
                if (depth == 0) return i;
            }
        }

        return -1;
    }

    private static int CountTopLevelArgs(string inside)
    {
        if (string.IsNullOrWhiteSpace(inside)) return 0;

        int depthParen = 0, depthBracket = 0, depthBrace = 0;
        bool inString = false;
        int commas = 0;
        bool hasNonWhitespace = false;

        for (int i = 0; i < inside.Length; i++)
        {
            char c = inside[i];

            if (c == '"' && (i == 0 || inside[i - 1] != '\\'))
                inString = !inString;

            if (inString) continue;

            if (!char.IsWhiteSpace(c)) hasNonWhitespace = true;

            switch (c)
            {
                case '(': depthParen++; break;
                case ')': depthParen--; break;
                case '[': depthBracket++; break;
                case ']': depthBracket--; break;
                case '{': depthBrace++; break;
                case '}': depthBrace--; break;
                case ',' when depthParen == 0 && depthBracket == 0 && depthBrace == 0:
                    commas++;
                    break;
            }
        }

        return hasNonWhitespace ? commas + 1 : 0;
    }

    private static (int Line, int Column) GetLineAndColumn(string text, int index)
    {
        int line = 1;
        int column = 1;

        for (int i = 0; i < index && i < text.Length; i++)
        {
            if (text[i] == '\n')
            {
                line++;
                column = 1;
                continue;
            }

            if (text[i] != '\r')
                column++;
        }

        return (line, column);
    }

    private static string ExtractSnippet(string text, int index)
    {
        int lineStart = text.LastIndexOf('\n', Math.Max(0, index - 1));
        lineStart = lineStart < 0 ? 0 : lineStart + 1;

        int lineEnd = text.IndexOf('\n', index);
        if (lineEnd < 0) lineEnd = text.Length;

        return text[lineStart..lineEnd].Trim();
    }

    private sealed class BuiltinStats(string name, string kind)
    {
        public string Name { get; } = name;
        public string Kind { get; } = kind;
        public int ObservedCalls { get; private set; }
        public int MinArgs { get; private set; } = int.MaxValue;
        public int MaxArgs { get; private set; }

        public void Observe(int argc)
        {
            ObservedCalls++;
            if (argc < MinArgs) MinArgs = argc;
            if (argc > MaxArgs) MaxArgs = argc;
        }
    }

    private sealed record CallObservation(
        string Name,
        int ArgCount,
        string FilePath,
        int Line,
        int Column,
        string Snippet,
        string ArgsText);

    private sealed record GameAliasMap(
        Dictionary<string, string> FunctionsById,
        Dictionary<string, string> MethodsById,
        Dictionary<string, string> TokensById);
}