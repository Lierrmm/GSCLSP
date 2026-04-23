using System.Text.Json;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Tools;

public static class GscToolBuiltInsLoader
{
    private const string RawBaseUrl = "https://raw.githubusercontent.com/xensik/gsc-tool/refs/heads/dev/src/gsc/engine";

    public static readonly HashSet<string> SupportedGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "iw5", "iw6", "iw7", "iw8", "iw9",
        "s1", "s2", "s4",
        "h1", "h2",
    };

    public static bool IsSupported(string game) => SupportedGames.Contains(game);

    public sealed record NameLists(IReadOnlyList<string> Functions, IReadOnlyList<string> Methods);

    public static string CachePath(string game)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gsclsp",
            "builtins");
        return Path.Combine(root, $"{game.ToLowerInvariant()}_gsctool.json");
    }

    public static bool IsCacheStale(string game, TimeSpan maxAge)
    {
        var path = CachePath(game);
        if (!File.Exists(path)) return true;
        return DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge;
    }

    public static NameLists? TryLoadFromCache(string game)
    {
        try
        {
            var path = CachePath(game);
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var functions = ReadArray(doc.RootElement, "functions");
            var methods = ReadArray(doc.RootElement, "methods");
            if (functions.Count == 0 && methods.Count == 0) return null;
            return new NameLists(functions, methods);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<NameLists?> FetchAsync(string game, CancellationToken ct = default)
    {
        var key = game.Trim().ToLowerInvariant();
        if (!IsSupported(key)) return null;

        try
        {
            using var http = new HttpClient();
            var funcTask = TryGetFirstAvailableAsync(http, ct,
                $"{RawBaseUrl}/{key}_func.cpp",
                $"{RawBaseUrl}/{key}_pc_func.cpp");
            var methTask = TryGetFirstAvailableAsync(http, ct,
                $"{RawBaseUrl}/{key}_meth.cpp",
                $"{RawBaseUrl}/{key}_pc_meth.cpp");

            await Task.WhenAll(funcTask, methTask);

            var funcSource = await funcTask;
            var methSource = await methTask;
            if (funcSource == null && methSource == null) return null;

            var functions = ParseNames(funcSource);
            var methods = ParseNames(methSource);
            if (functions.Count == 0 && methods.Count == 0) return null;

            SaveCache(key, functions, methods);
            return new NameLists(functions, methods);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string?> TryGetFirstAvailableAsync(HttpClient http, CancellationToken ct, params string[] urls)
    {
        foreach (var url in urls)
        {
            ct.ThrowIfCancellationRequested();
            try { return await http.GetStringAsync(url, ct); }
            catch (OperationCanceledException) { throw; }
            catch { }
        }
        return null;
    }

    private static List<string> ParseNames(string? source)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(source)) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in GscToolRegex().Matches(source))
        {
            var name = m.Groups["name"].Value;
            if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                result.Add(name);
        }
        return result;
    }

    private static List<string> ReadArray(JsonElement root, string propertyName)
    {
        var result = new List<string>();
        if (!root.TryGetProperty(propertyName, out var arr) || arr.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var entry in arr.EnumerateArray())
        {
            if (entry.ValueKind == JsonValueKind.String)
            {
                var value = entry.GetString();
                if (!string.IsNullOrWhiteSpace(value)) result.Add(value);
            }
        }
        return result;
    }

    private static void SaveCache(string game, IEnumerable<string> functions, IEnumerable<string> methods)
    {
        try
        {
            var path = CachePath(game);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new { functions = functions.ToArray(), methods = methods.ToArray() };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
