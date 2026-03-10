using GSCLSP.Core.Models;
using GSCLSP.Core.Parsing;
using GSCLSP.Core.Services;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Indexing;

public partial class GscIndexer
{
    private readonly List<GscSymbol> _symbols = [];
    public IEnumerable<GscSymbol> Symbols => _symbols;
    public int SymbolCount => _symbols.Count;
    public BuiltInProvider BuiltIns { get; } = new();
    public string? _dumpPath { get; private set; }
    public List<GscSymbol> WorkspaceSymbols { get; private set; } = [];
    private readonly Dictionary<string, GscFileMap> _workspaceFileMaps = [];
    private readonly Dictionary<string, GscFileMap> _fileMaps = [];

    public static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        // Handle URI or File System Path
        string clean = path.StartsWith("file://")
            ? Uri.UnescapeDataString(new Uri(path).LocalPath)
            : path;

        return clean
            .Replace("\\", "/")
            .TrimStart('/')
            .ToLower()
            .Trim();
    }

    public void ExportIndexToJson(string dumpPath, string outputPath)
    {
        IndexFolder(dumpPath);
        JsonSerializerOptions jsonSerializerOptions = new() { WriteIndented = true };
        JsonSerializerOptions options = jsonSerializerOptions;
        var json = JsonSerializer.Serialize(_symbols, options);
        File.WriteAllText(outputPath, json);
    }

    public TimeSpan IndexFolder(string folderPath)
    {
        _symbols.Clear();
        var sw = Stopwatch.StartNew();

        if (Directory.Exists(folderPath))
        {
            var files = Directory.GetFiles(folderPath, "*.*", SearchOption.AllDirectories)
                .Where(s => s.EndsWith(".gsc") || s.EndsWith(".gsh"));

            foreach (var file in files)
            {
                ParseFile(file);
            }
        }

        sw.Stop();
        return sw.Elapsed;
    }

    public void UpdateDumpPath(string? newPath)
    {
        if (string.IsNullOrWhiteSpace(newPath) || !Directory.Exists(newPath)) return;
        string cacheFile = Path.Combine(newPath, "symbols.json");

        _dumpPath = newPath;
        _symbols.Clear();
        _fileMaps.Clear();

        Task.Run(() =>
        {
            if (File.Exists(cacheFile))
            {
                Console.Error.WriteLine($"GSCLSP: Found existing cache. Loading...");
                LoadGlobalIndex(cacheFile);
            }
            else
            {
                Console.Error.WriteLine($"GSCLSP: No cache found. Crawling folder...");
                IndexFolder(newPath);
                ExportIndexToJson(newPath, cacheFile);

                Console.Error.WriteLine($"GSCLSP: Created new cache with {_symbols.Count} symbols.");
                LoadGlobalIndex(cacheFile);
            }
        });
    }

    public void LoadGlobalIndex(string jsonPath)
    {
        if (!File.Exists(jsonPath)) return;

        var data = File.ReadAllText(jsonPath);
        var symbols = JsonSerializer.Deserialize(data, GscJsonContext.Default.ListGscSymbol);

        if (symbols != null)
        {
            _symbols.AddRange(symbols);
            foreach (var s in symbols)
            {
                if (!_fileMaps.ContainsKey(s.FilePath))
                    _fileMaps[s.FilePath] = new GscFileMap { FilePath = s.FilePath };

                _fileMaps[s.FilePath].LocalSymbols.Add(s);
            }
        }

        Console.Error.WriteLine($"GSCLSP: Indexer loaded {_symbols.Count} symbols from JSON.");
    }

    private void ParseFile(string path)
    {
        var fileMap = new GscFileMap { FilePath = path };
        var lines = File.ReadLines(path);
        int lineNum = 0;

        foreach (var line in lines)
        {
            lineNum++;

            var includeMatch = IncludeRegex().Match(line);
            if (includeMatch.Success)
            {
                fileMap.Includes.Add(includeMatch.Groups[1].Value.Replace("\\", "/"));
                continue;
            }

            var funcMatch = FunctionLineRegex().Match(line);
            if (funcMatch.Success)
            {
                var symbol = new GscSymbol(
                    funcMatch.Groups[1].Value,
                    path,
                    lineNum,
                    funcMatch.Groups[2].Value,
                    SymbolType.Function
                );
                fileMap.LocalSymbols.Add(symbol);
                _symbols.Add(symbol); // Still keep global list for fast search
            }
        }
        _fileMaps[path.Replace("\\", "/")] = fileMap;
    }

    public async Task<string?> GetIncludePath(string includeString)
    {
        if (string.IsNullOrWhiteSpace(includeString)) return null;

        string normalized = includeString.Replace("\\", "/").ToLower();

        var extensions = new[] { ".gsc", ".gsh" };

        foreach (var ext in extensions)
        {
            string searchSuffix = normalized;
            if (!searchSuffix.EndsWith(ext)) searchSuffix += ext;

            var workspaceMatch = _workspaceFileMaps.Values.FirstOrDefault(f =>
                f.FilePath.Replace("\\", "/").ToLower().EndsWith(searchSuffix));
            if (workspaceMatch != null) return workspaceMatch.FilePath;

            var dumpMatch = _fileMaps.Values.FirstOrDefault(f =>
                f.FilePath.Replace("\\", "/").ToLower().EndsWith(searchSuffix));
            if (dumpMatch != null) return dumpMatch.FilePath;
        }

        return null;
    }

    public GscResolution ResolveFunction(string callingFilePath, string functionName)
    {
        string normalizedCallingPath = Uri.UnescapeDataString(callingFilePath)
            .Replace("file:///", "")
            .Replace("\\", "/")
            .ToLower()
            .Trim();

        Console.Error.WriteLine($"GSCLSP: Resolving '{functionName}' for {normalizedCallingPath}");

        var currentFileLocal = WorkspaceSymbols.FirstOrDefault(s =>
            s.FilePath.Replace("\\", "/").Equals(normalizedCallingPath, StringComparison.OrdinalIgnoreCase) &&
            s.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

        if (currentFileLocal != null)
        {
            return new GscResolution(currentFileLocal, ResolutionType.Local, normalizedCallingPath);
        }

        // Built-ins like 'distance' or 'isDefined' override everything except local definitions.
        var builtIn = BuiltIns.GetBuiltIn(functionName);
        if (builtIn != null)
        {
            return new GscResolution(builtIn, ResolutionType.Global);
        }

        // If the user typed maps\mp\path::func, we look ONLY in that file.
        if (functionName.Contains("::"))
        {
            var parts = functionName.Split("::");
            string explicitPath = parts[0].Replace("\\", "/");
            string funcName = parts[1];

            var target = WorkspaceSymbols.Concat(Symbols).FirstOrDefault(s =>
                s.FilePath.Replace("\\", "/").ToLower().EndsWith(explicitPath + ".gsc") &&
                s.Name.Equals(funcName, StringComparison.OrdinalIgnoreCase));

            if (target != null)
                return new GscResolution(target, ResolutionType.Included, target.FilePath);
        }

        // Check files that are explicitly included in the current file's header.
        if (_workspaceFileMaps.TryGetValue(normalizedCallingPath, out var map) || 
            _fileMaps.TryGetValue(normalizedCallingPath, out map))
        {
            foreach (var includePath in map.Includes)
            {
                string searchSuffix = includePath.ToLower();
                if (!searchSuffix.EndsWith(".gsc")) searchSuffix += ".gsc";

                // Search for the included file in BOTH maps
                var includedFile = _workspaceFileMaps.Values.Concat(_fileMaps.Values).FirstOrDefault(f =>
                    f.FilePath.Replace("\\", "/").ToLower().EndsWith(searchSuffix));

                if (includedFile != null)
                {
                    var found = includedFile.LocalSymbols.FirstOrDefault(s =>
                        s.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

                    if (found != null) return new GscResolution(found, ResolutionType.Included, includedFile.FilePath);
                }
            }
        }

        // If nothing else works, search the entire project index
        var globalMatch = WorkspaceSymbols.Concat(Symbols).FirstOrDefault(s =>
            s.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

        if (globalMatch != null)
            return new GscResolution(globalMatch, ResolutionType.Included, globalMatch.FilePath);

        Console.Error.WriteLine($"GSCLSP: '{functionName}' could not be resolved.");
        return new GscResolution(null, ResolutionType.NotFound);
    }

    public static GscSymbol? ScanFileForFunction(string filePath, string functionName)
    {
        try
        {
            var lines = File.ReadAllLines(filePath);

            // ^ Must be the absolute start of the line (no indentation for definitions)
            // {name} The name
            // \s*\( The parenthesis
            // [^)]* The params
            // \) Closing parenthesis
            // \s*$ NOTHING else allowed on the line except whitespace
            string strictDefinitionPattern = $@"^{Regex.Escape(functionName)}\s*\(([^)]*)\)\s*$";

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i]; // Don't trim yet, we need to check start of line

                // If it's indented, it's likely a call inside a function, not a definition
                if (line.StartsWith(' ') || line.StartsWith('\t')) continue;

                // If it contains a semicolon, it's definitely a call
                if (line.Contains(';')) continue;

                var match = Regex.Match(line, strictDefinitionPattern, RegexOptions.IgnoreCase);
                if (match.Success)
                {
                    bool hasBrace = line.Contains('{') || (i + 1 < lines.Length && lines[i + 1].Trim().StartsWith('{'));
                    if (!hasBrace) continue;

                    List<string> commentLines = [];
                    bool inBlockComment = false;

                    for (int j = i - 1; j >= 0; j--)
                    {
                        string prevLine = lines[j].Trim();

                        // Handle end of a block comment
                        if (prevLine.EndsWith("*/")) { inBlockComment = true; prevLine = prevLine.Replace("*/", ""); }

                        // Handle start of a block comment
                        if (prevLine.StartsWith("/*")) { inBlockComment = false; break; }

                        if (inBlockComment || prevLine.StartsWith("//"))
                        {
                            string cleanLine = prevLine
                                .TrimStart('/', '*', ' ')   // Remove comment markers
                                .Replace("\"", "")          // Remove ScriptDoc quotes
                                .Replace("ScriptDocBegin", "")
                                .Replace("ScriptDocEnd", "")
                                .Trim();

                            if (!string.IsNullOrWhiteSpace(cleanLine) && !cleanLine.StartsWith("==="))
                            {
                                commentLines.Insert(0, cleanLine);
                            }
                        }
                        else if (string.IsNullOrWhiteSpace(prevLine)) continue;
                        else break;
                    }
                    string doc = string.Join("  \n", commentLines);

                    return new GscSymbol(
                        Name: functionName,
                        FilePath: filePath,
                        LineNumber: i + 1,
                        Parameters: match.Groups[1].Value.Trim(),
                        Type: SymbolType.Function,
                        Documentation: doc // Pass it here
                    );
                }
            }
        }
        catch { }
        return null;
    }

    public GscResolution ResolveFromLine(string contextPath, string rawLine)
    {
        var parsed = GscLineParser.ExtractCall(rawLine);
        if (parsed == null) return new GscResolution(null, ResolutionType.NotFound);

        // If there is a path (like maps\mp\_persistence), we reconstruct the :: string
        string lookupName = string.IsNullOrEmpty(parsed.Path)
            ? parsed.FunctionName
            : $"{parsed.Path}::{parsed.FunctionName}";

        return ResolveFunction(contextPath, lookupName);
    }

    public void FindReferences(string functionName)
    {
        var sw = Stopwatch.StartNew();
        int count = 0;

        // We search for the function name followed by a parenthesis
        // to avoid catching variable names that happen to match.
        string pattern = $@"\b{Regex.Escape(functionName)}\s*\(";

        // Iterate through every file we've indexed
        foreach (var fileMap in _fileMaps.Values)
        {
            var lines = File.ReadLines(fileMap.FilePath);
            int lineNum = 0;
            foreach (var line in lines)
            {
                lineNum++;
                if (Regex.IsMatch(line, pattern, RegexOptions.IgnoreCase))
                {
                    Console.WriteLine($"[REF] {Path.GetFileName(fileMap.FilePath)}:{lineNum} -> {line.Trim()}");
                    count++;
                }
            }
        }

        Console.WriteLine($"\nFound {count} references in {sw.Elapsed.TotalMilliseconds:N2}ms.");
    }

    public void UpdateFile(string path, string content)
    {
        path = path.Replace("\\", "/");

        // Remove old symbols for this file from the global list
        _symbols.RemoveAll(s => s.FilePath.Replace("\\", "/").Equals(path, StringComparison.OrdinalIgnoreCase));

        var fileMap = new GscFileMap { FilePath = path };
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        int lineNum = 0;

        foreach (var line in lines)
        {
            lineNum++;

            // Match Includes
            var includeMatch = IncludeRegex().Match(line);
            if (includeMatch.Success)
            {
                fileMap.Includes.Add(includeMatch.Groups[1].Value.Replace("\\", "/"));
                continue;
            }

            // Match Functions
            var funcMatch = FunctionLineRegex().Match(line);
            if (funcMatch.Success)
            {
                var symbol = new GscSymbol(
                    funcMatch.Groups[1].Value,
                    path.Replace("\\", "/").ToLower().Trim(),
                    lineNum,
                    funcMatch.Groups[2].Value,
                    SymbolType.Function
                );
                fileMap.LocalSymbols.Add(symbol);
                _symbols.Add(symbol);
            }
        }

        _fileMaps[path] = fileMap;
    }

    public bool IsKnownFunction(string scriptPath, string functionName)
    {
        string searchSuffix = scriptPath.Replace("\\", "/");
        if (!searchSuffix.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase))
            searchSuffix += ".gsc";

        var file = _fileMaps.Values.FirstOrDefault(f =>
            f.FilePath.Replace("\\", "/").EndsWith(searchSuffix, StringComparison.OrdinalIgnoreCase));

        return file?.LocalSymbols.Any(s =>
            s.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase)) ?? false;
    }

    public bool IsKnownPath(string scriptPath)
    {
        string searchSuffix = scriptPath.Replace("\\", "/");
        if (!searchSuffix.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase))
            searchSuffix += ".gsc";

        return _fileMaps.Keys.Any(k =>
            k.Replace("\\", "/").EndsWith(searchSuffix, StringComparison.OrdinalIgnoreCase));
    }

    public IEnumerable<GscSymbol> GetSymbolsByName(string name) =>
        _symbols.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<GscSymbol> Search(string query) =>
        _symbols.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

    public IEnumerable<string> GetUniqueSymbolNames() => _symbols.Select(s => s.Name).Distinct();

    public IEnumerable<GscSymbol> GetSymbolsByPath(string scriptPath)
    {
        // Normalize user input: 'common_scripts\utility' -> 'common_scripts/utility'
        string searchPath = scriptPath.Replace("\\", "/").ToLower();

        return _symbols.Where(s => {
            string entryPath = s.FilePath.Replace("\\", "/").ToLower();

            // Match if the entry is 'maps/mp/gametypes/_globallogic.gsc' 
            // and user typed 'gametypes/_globallogic::'
            return entryPath.Contains(searchPath) ||
                   Path.GetFileNameWithoutExtension(entryPath) == searchPath;
        });
    }

    public void IndexWorkspace(string workspacePath)
    {
        Console.Error.WriteLine($"Indexing Workspace {workspacePath}");
        var localSymbols = new List<GscSymbol>();

        if (!Directory.Exists(workspacePath)) return;

        _workspaceFileMaps.Clear();
        var files = Directory.GetFiles(workspacePath, "*.*", SearchOption.AllDirectories)
            .Where(s => s.EndsWith(".gsc") || s.EndsWith(".gsh"));

        foreach (var file in files)
        {
            var content = File.ReadAllText(file);
            string normalizedPath = file.Replace("\\", "/").ToLower();
            var fileMap = new GscFileMap { FilePath = file };

            var includeMatches = IncludeRegex().Matches(content);
            foreach (Match inc in includeMatches)
            {
                fileMap.Includes.Add(inc.Groups[1].Value.Replace("\\", "/"));
            }

            var matches = FunctionDefinitionRegex().Matches(content);

            foreach (Match match in matches)
            {
                var symbol = new GscSymbol(
                    match.Groups[1].Value,
                    file,
                    GetLineNumberFromIndex(content, match.Index),
                    match.Groups[2].Value,
                    SymbolType.Function
                );

                localSymbols.Add(symbol);
                fileMap.LocalSymbols.Add(symbol);
            }

            _workspaceFileMaps[normalizedPath] = fileMap;
        }

        WorkspaceSymbols = localSymbols;
    }

    private static int GetLineNumberFromIndex(string content, int index)
    {
        int lineCount = 0;
        for (int i = 0; i < index; i++)
        {
            if (content[i] == '\n') lineCount++;
        }
        return lineCount;
    }
}