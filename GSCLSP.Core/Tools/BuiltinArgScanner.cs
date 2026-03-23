using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Tools;

public static partial class BuiltinArgScanner
{
    private static readonly Regex CallRegex = CallFuncRegex();

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

        var stats = new Dictionary<string, BuiltinStats>(StringComparer.OrdinalIgnoreCase);
        var observations = string.IsNullOrWhiteSpace(matchesLogPath) ? null : new List<CallObservation>();

        Register(functions, "function", stats);
        Register(methods, "method", stats);

        foreach (var file in Directory.EnumerateFiles(dumpFolder, "*.gsc", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            string text = await File.ReadAllTextAsync(file, ct);
            ScanText(file, text, stats, observations, matchNameFilter);
        }

        Apply(functions, stats, includeArgsArray);
        Apply(methods, stats, includeArgsArray);

        var options = new JsonSerializerOptions { WriteIndented = true };
        await File.WriteAllTextAsync(builtinsJsonPath, root.ToJsonString(options), ct);

        if (observations is { Count: > 0 } && !string.IsNullOrWhiteSpace(matchesLogPath))
        {
            var dir = Path.GetDirectoryName(matchesLogPath);
            if (!string.IsNullOrWhiteSpace(dir))
                Directory.CreateDirectory(dir);

            var sorted = observations
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.FilePath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(o => o.Line)
                .ToList();

            await File.WriteAllTextAsync(matchesLogPath, JsonSerializer.Serialize(sorted, options), ct);
        }
    }

    private static void Register(JsonArray arr, string kind, Dictionary<string, BuiltinStats> stats)
    {
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;
            string? name = obj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;

            if (!stats.ContainsKey(name))
                stats[name] = new BuiltinStats(name, kind);
        }
    }

    private static void ScanText(
        string filePath,
        string text,
        Dictionary<string, BuiltinStats> stats,
        List<CallObservation>? observations,
        string? matchNameFilter)
    {
        foreach (Match m in CallRegex.Matches(text))
        {
            string name = m.Groups["name"].Value;
            if (!stats.TryGetValue(name, out var s)) continue;

            int openParen = m.Index + m.Length - 1;
            int closeParen = FindMatchingParen(text, openParen);
            if (closeParen < 0) continue;

            string inside = text[(openParen + 1)..closeParen];
            int argc = CountTopLevelArgs(inside);
            s.Observe(argc);

            if (observations is not null &&
                (string.IsNullOrWhiteSpace(matchNameFilter) || name.Equals(matchNameFilter, StringComparison.OrdinalIgnoreCase)))
            {
                var (line, column) = GetLineAndColumn(text, m.Index);
                observations.Add(new CallObservation(
                    Name: name,
                    ArgCount: argc,
                    FilePath: filePath,
                    Line: line,
                    Column: column,
                    Snippet: ExtractSnippet(text, m.Index),
                    ArgsText: inside.Trim()));
            }
        }
    }

    private static void Apply(JsonArray arr, Dictionary<string, BuiltinStats> stats, bool includeArgsArray)
    {
        foreach (var node in arr)
        {
            if (node is not JsonObject obj) continue;
            string? name = obj["name"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(name)) continue;
            if (!stats.TryGetValue(name, out var s) || s.ObservedCalls == 0) continue;

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
}