using System.Text.Json;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Tools;

public static class GscToolBuiltInsLoader
{
    public static readonly HashSet<string> SupportedGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "iw5", "iw6", "iw7", "iw8", "iw9",
        "s1", "s2", "s4",
        "h1", "h2",
    };

    private static readonly HttpClient http = new()
    {
        Timeout = TimeSpan.FromSeconds(120)
    };

    public static bool IsSupported(string game) => SupportedGames.Contains(game);

    public sealed record NameLists(IReadOnlyList<string> Functions, IReadOnlyList<string> Methods, IReadOnlyList<string> Tokens);

    public static string CachePath(string game)
    {
        var root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "gsclsp",
            "builtins");
        return Path.Combine(root, $"{game.ToLowerInvariant()}_gsctool.json");
    }

    public static bool IsCacheStale(string game, GscToolSource source, TimeSpan maxAge)
    {
        var path = CachePath(game);
        if (!File.Exists(path)) return true;
        if (DateTime.UtcNow - File.GetLastWriteTimeUtc(path) > maxAge) return true;

        var cachedSource = TryReadCachedSource(path);
        return !source.Equals(cachedSource);
    }

    public static NameLists? TryLoadFromCache(string game, GscToolSource source)
    {
        try
        {
            var path = CachePath(game);
            if (!File.Exists(path)) return null;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var cachedSource = ReadSourceElement(doc.RootElement) ?? GscToolSource.Default;
            if (!source.Equals(cachedSource)) return null;

            var functions = ReadArray(doc.RootElement, "functions");
            var methods = ReadArray(doc.RootElement, "methods");
            var tokens = ReadArray(doc.RootElement, "tokens");
            if (functions.Count == 0 && methods.Count == 0) return null;
            return new NameLists(functions, methods, tokens);
        }
        catch
        {
            return null;
        }
    }

    public static async Task<NameLists?> FetchAsync(string game, GscToolSource source, CancellationToken ct = default)
    {
        var key = game.Trim().ToLowerInvariant();
        if (!IsSupported(key)) return null;

        try
        {
            var baseUrl = source.EngineDirectoryUrl;
            var funcTask = TryGetFirstAvailableAsync(ct, baseUrl,
                $"{key}_func.cpp",
                $"{key}_pc_func.cpp");
            var methTask = TryGetFirstAvailableAsync(ct, baseUrl,
                $"{key}_meth.cpp",
                $"{key}_pc_meth.cpp");
            var tokTask = TryGetFirstAvailableAsync(ct, baseUrl,
                $"{key}_token.cpp",
                $"{key}_pc_token.cpp");

            var fetched = await Task.WhenAll(funcTask, methTask, tokTask);

            if (fetched.All(string.IsNullOrEmpty)) return null;

            var funcSource = fetched[0];
            var methSource = fetched[1];
            var tokSource = fetched[2];

            var functions = ParseNames(funcSource);
            var methods = ParseNames(methSource);
            var tokens = ParseNames(tokSource);

            tokens = [.. tokens.Where(t => !functions.Contains(t, StringComparer.OrdinalIgnoreCase) &&
                                  !methods.Contains(t, StringComparer.OrdinalIgnoreCase) || !t.Contains('/'))];

            if (functions.Count == 0 && methods.Count == 0 && tokens.Count == 0) return null;

            SaveCache(key, source, functions, methods, tokens);
            return new NameLists(functions, methods, tokens);
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

    private static async Task<string?> TryGetFirstAvailableAsync(CancellationToken ct, string baseUrl, params string[] paths)
    {
        foreach (var p in paths)
        {
            ct.ThrowIfCancellationRequested();
            try { return await http.GetStringAsync(baseUrl + p, ct); }
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

    private static GscToolSource? TryReadCachedSource(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            return ReadSourceElement(doc.RootElement) ?? GscToolSource.Default;
        }
        catch
        {
            return null;
        }
    }

    private static GscToolSource? ReadSourceElement(JsonElement root)
    {
        if (!root.TryGetProperty("source", out var src) || src.ValueKind != JsonValueKind.Object)
            return null;
        if (!src.TryGetProperty("owner", out var owner) || owner.ValueKind != JsonValueKind.String)
            return null;
        if (!src.TryGetProperty("repo", out var repo) || repo.ValueKind != JsonValueKind.String)
            return null;
        if (!src.TryGetProperty("branch", out var branch) || branch.ValueKind != JsonValueKind.String)
            return null;

        var ownerStr = owner.GetString();
        var repoStr = repo.GetString();
        var branchStr = branch.GetString();
        if (string.IsNullOrWhiteSpace(ownerStr) || string.IsNullOrWhiteSpace(repoStr) || string.IsNullOrWhiteSpace(branchStr))
            return null;

        return new GscToolSource(ownerStr, repoStr, branchStr);
    }

    private static void SaveCache(string game, GscToolSource source, IEnumerable<string> functions, IEnumerable<string> methods, IEnumerable<string> tokens /* Unused */)
    {
        try
        {
            var path = CachePath(game);
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = new
            {
                source = new { owner = source.Owner, repo = source.Repo, branch = source.Branch },
                functions = functions.ToArray(),
                methods = methods.ToArray()
            };
            File.WriteAllText(path, JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }
}
