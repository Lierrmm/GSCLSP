using GSCLSP.Core.Diagnostics;
using GSCLSP.Core.Models;
using GSCLSP.Core.Parsing;
using GSCLSP.Core.Services;
using GSCLSP.Core.Tools;
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
    public string? DumpPath { get; private set; }
    public string CurrentGame { get; private set; } = "iw4";
    public event Action<string>? GameChanged;
    public List<GscSymbol> WorkspaceSymbols { get; private set; } = [];
    private readonly Dictionary<string, GscFileMap> _workspaceFileMaps = [];
    private readonly Dictionary<string, GscFileMap> _fileMaps = [];

    // File watching and caching
    private FileSystemWatcher? _fileWatcher;
    public string? WorkspacePath { get; private set; }
    private readonly Dictionary<string, string> _fileContentCache = [];
    private readonly HashSet<string> _pendingChanges = [];
    private readonly Lock _pendingChangesLock = new();
    private System.Timers.Timer? _debounceTimer;
    private System.Timers.Timer? _configDebounceTimer;
    private const int DEBOUNCE_MS = 300;

    // Memoization cache for ScanFileForFunction
    private static readonly Dictionary<string, GscSymbol?> _scanFunctionCache = [];
    private static readonly Lock _scanCacheLock = new();

    public record LocalVariable(string Name, string Value, int Line);
    private static readonly Dictionary<string, List<LocalVariable>> _localVarCache = [];
    private static readonly Lock _localVarCacheLock = new();

    public record MacroDefinition(string Name, string Value, string FilePath, int Line);
    private static readonly Dictionary<string, List<MacroDefinition>> _macroCache = [];
    private static readonly Lock _macroCacheLock = new();

    private string? _settingDumpPath;

    public static string NormalizePath(string? path)
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
                .Where(IsScriptFile);

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
        if (string.IsNullOrWhiteSpace(newPath) || !Directory.Exists(newPath))
        {
            DumpPath = null;
            ClearGlobalIndexAndCaches();
            return;
        }

        string cacheFile = Path.Combine(newPath, "symbols.json");

        DumpPath = newPath;
        ClearGlobalIndexAndCaches();

        Task _ = Task.Run(() =>
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
        var lines = File.ReadAllLines(path);

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            var lineNum = lineIndex + 1;

            var includeMatch = IncludeRegex().Match(line);
            if (includeMatch.Success)
            {
                fileMap.Includes.Add(includeMatch.Groups[1].Value.Replace("\\", "/"));
                continue;
            }

            var inlineMatch = InlinePathRegex().Match(line);
            if (inlineMatch.Success)
            {
                fileMap.Inlines.Add(inlineMatch.Groups[1].Value.Replace("\\", "/"));
                continue;
            }

            if (!IsFunctionDefinitionLine(lines, lineIndex, out var funcMatch))
                continue;

            var name = funcMatch.Groups["name"].Value;
            var rawParams = funcMatch.Groups["params"].Value;
            var cleanParams = CleanGscParams(rawParams);

            var symbol = new GscSymbol(
                name,
                path,
                lineNum,
                cleanParams,
                SymbolType.Function
            );
            fileMap.LocalSymbols.Add(symbol);
            _symbols.Add(symbol);
        }

        _fileMaps[NormalizePathKey(path)] = fileMap;
    }

    private static bool IsFunctionDefinitionLine(string[] lines, int lineIndex, out Match functionMatch)
    {
        functionMatch = Match.Empty;

        if (lineIndex < 0 || lineIndex >= lines.Length)
            return false;

        var line = lines[lineIndex];
        var codeLine = StripTrailingLineComment(line);

        if (codeLine.Length == 0 || char.IsWhiteSpace(codeLine[0]))
            return false;

        if (codeLine.Contains(';'))
            return false;

        var match = FunctionMultiLineRegex().Match(codeLine);
        if (!match.Success || match.Index != 0)
            return false;

        var name = match.Groups["name"].Value;
        if (GscLanguageKeywords.DiagnosticReservedWords.Contains(name))
            return false;

        var trailing = codeLine[(match.Index + match.Length)..].Trim();
        if (!string.IsNullOrEmpty(trailing) &&
            !trailing.StartsWith("{", StringComparison.Ordinal) &&
            !trailing.StartsWith("//", StringComparison.Ordinal))
            return false;

        if (codeLine.Contains('{'))
        {
            functionMatch = match;
            return true;
        }

        for (int i = lineIndex + 1; i < lines.Length; i++)
        {
            var trimmed = StripTrailingLineComment(lines[i]).Trim();

            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (trimmed.StartsWith("/*", StringComparison.Ordinal) ||
                trimmed.StartsWith("*", StringComparison.Ordinal) ||
                trimmed.EndsWith("*/", StringComparison.Ordinal))
                continue;

            if (trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                functionMatch = match;
                return true;
            }

            return false;
        }

        return false;
    }

    private static string StripTrailingLineComment(string line)
    {
        if (string.IsNullOrEmpty(line))
            return string.Empty;

        var inString = false;

        for (int i = 0; i < line.Length - 1; i++)
        {
            if (line[i] == '"' && (i == 0 || line[i - 1] != '\\'))
            {
                inString = !inString;
                continue;
            }

            if (!inString && line[i] == '/' && line[i + 1] == '/')
                return line[..i].TrimEnd();
        }

        return line;
    }

    private static string CleanGscParams(string raw)
    {
        string noComments = CommentRegex().Replace(raw, "");
        noComments = MultilineCommentRegex().Replace(noComments, "");
        string flat = WhiteSpaceTabRegex().Replace(noComments, " ").Trim();
        return flat;
    }

    public async Task<string?> GetIncludePathAsync(string includeString)
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

    public GscResolution ResolveFunction(string callingFilePath, string functionName, bool preferMethodBuiltIn = false)
    {
        string normalizedCallingPath = Uri.UnescapeDataString(callingFilePath)
            .Replace("file:///", "")
            .Replace("\\", "/")
            .ToLower()
            .Trim();

        var currentFileLocal = WorkspaceSymbols.FirstOrDefault(s =>
            s.FilePath.Replace("\\", "/").Equals(normalizedCallingPath, StringComparison.OrdinalIgnoreCase) &&
            s.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase));

        if (currentFileLocal != null)
        {
            return new GscResolution(currentFileLocal, ResolutionType.Local, normalizedCallingPath);
        }

        // Built-ins like 'distance' or 'isDefined' override everything except local definitions.
        var builtIn = BuiltIns.GetBuiltIn(functionName, preferMethodBuiltIn);
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
        // Create cache key: "filepath|functionname"
        string cacheKey = $"{filePath}|{functionName}";

        // Check cache first
        lock (_scanCacheLock)
        {
            if (_scanFunctionCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        // Not in cache, perform expensive scan
        GscSymbol? result = ScanFileForFunctionUncached(filePath, functionName);

        // Store in cache
        lock (_scanCacheLock)
        {
            _scanFunctionCache[cacheKey] = result;
        }

        return result;
    }

    private static GscSymbol? ScanFileForFunctionUncached(string filePath, string functionName)
    {
        try
        {
            if (!File.Exists(filePath))
                return null;

            // Read all lines once. We need random access to scan backwards for comments.
            var lines = File.ReadAllLines(filePath);
            if (lines.Length == 0) return null;

            var fnName = functionName ?? string.Empty;
            var fnLen = fnName.Length;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                string codeLine = StripTrailingLineComment(line);

                if (string.IsNullOrEmpty(codeLine))
                    continue;

                // Skip indented lines (not top-level function defs) and lines with semicolons
                if (char.IsWhiteSpace(codeLine[0]))
                    continue;

                if (codeLine.Contains(';'))
                    continue;

                // Quick reject: line must start with function name (case-insensitive)
                if (fnLen == 0 || codeLine.Length < fnLen)
                    continue;

                if (!codeLine.StartsWith(fnName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // Next character after name should be whitespace or '('
                if (codeLine.Length == fnLen)
                    continue;

                int pos = fnLen;
                // skip whitespace
                while (pos < codeLine.Length && char.IsWhiteSpace(codeLine[pos])) pos++;
                if (pos >= codeLine.Length || codeLine[pos] != '(')
                    continue;

                // Find closing ')' on the same line
                int closeParen = codeLine.IndexOf(')', pos + 1);
                if (closeParen < 0)
                    continue;

                // Extract parameters substring
                string paramsText = codeLine[(pos + 1)..closeParen].Trim();

                // Ensure function has a body: either '{' on same line or next non-comment line starts with '{'
                bool hasBrace = codeLine.Contains('{');
                if (!hasBrace)
                {
                    int nextLine = i + 1;
                    while (nextLine < lines.Length)
                    {
                        var nextCode = StripTrailingLineComment(lines[nextLine]).Trim();
                        if (string.IsNullOrEmpty(nextCode)) { nextLine++; continue; }
                        if (nextCode.StartsWith("/*") || nextCode.StartsWith("*")) { nextLine++; continue; }
                        hasBrace = nextCode.StartsWith("{");
                        break;
                    }
                }

                if (!hasBrace) continue;

                // Collect documentation comments immediately above definition
                List<string>? commentLines = null;
                bool inBlockComment = false;
                bool foundComment = false;

                for (int j = i - 1; j >= 0; j--)
                {
                    string prevRaw = lines[j];
                    string prevLine = prevRaw.Trim();

                    if (string.IsNullOrWhiteSpace(prevLine))
                        break;

                    if (prevLine.EndsWith("*/", StringComparison.Ordinal))
                    {
                        inBlockComment = true;
                        foundComment = true;
                        prevLine = prevLine.Substring(0, prevLine.Length - 2);
                    }

                    if (prevLine.StartsWith("//", StringComparison.Ordinal))
                    {
                        foundComment = true;
                        commentLines ??= [];
                        string cleanLine = prevLine
                            .TrimStart('/', '*', ' ')
                            .Replace("\"", "")
                            .Replace("ScriptDocBegin", "")
                            .Replace("ScriptDocEnd", "")
                            .Trim();

                        if (!string.IsNullOrWhiteSpace(cleanLine) && !cleanLine.StartsWith("==="))
                            commentLines.Insert(0, cleanLine);

                        continue;
                    }

                    if (inBlockComment || prevLine.StartsWith("/*", StringComparison.Ordinal))
                    {
                        foundComment = true;
                        commentLines ??= [];
                        string cleanLine = prevLine
                            .Replace("/*", "")
                            .Replace("*/", "")
                            .TrimStart('*', ' ')
                            .Replace("\"", "")
                            .Replace("ScriptDocBegin", "")
                            .Replace("ScriptDocEnd", "")
                            .Trim();

                        if (!string.IsNullOrWhiteSpace(cleanLine) && !cleanLine.StartsWith("==="))
                            commentLines.Insert(0, cleanLine);

                        if (prevLine.StartsWith("/*", StringComparison.Ordinal))
                            break;

                        continue;
                    }

                    if (foundComment)
                        break;

                    break;
                }

                string doc = commentLines != null && commentLines.Count > 0
                    ? string.Join("  \n", commentLines)
                    : string.Empty;

                return new GscSymbol(
                    Name: fnName,
                    FilePath: filePath,
                    LineNumber: i + 1,
                    Parameters: paramsText,
                    Type: SymbolType.Function,
                    Documentation: doc
                );
            }
        }
        catch { }

        return null;
    }

    public static string? FindEnclosingFunctionName(string[] lines, int cursorLine)
    {
        for (int i = cursorLine; i >= 0; i--)
        {
            if (IsFunctionDefinitionLine(lines, i, out var match))
                return match.Groups["name"].Value;
        }

        return null;
    }

    public static List<LocalVariable> GetLocalVariables(string filePath, string functionName, string[] lines, int cursorLine)
    {
        string cacheKey = $"{filePath}|{functionName}";

        lock (_localVarCacheLock)
        {
            if (_localVarCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var result = ScanLocalVariablesForFunction(lines, cursorLine);

        lock (_localVarCacheLock)
        {
            _localVarCache[cacheKey] = result;
        }

        return result;
    }

    private static List<LocalVariable> ScanLocalVariablesForFunction(string[] lines, int cursorLine)
    {
        int funcDefLine = -1;
        Match? funcMatch = null;
        for (int i = cursorLine; i >= 0; i--)
        {
            if (IsFunctionDefinitionLine(lines, i, out var m))
            {
                funcDefLine = i;
                funcMatch = m;
                break;
            }
        }
        if (funcDefLine < 0) return [];

        int braceStart = -1;
        for (int i = funcDefLine; i < lines.Length; i++)
        {
            if (lines[i].Contains('{')) { braceStart = i; break; }
        }
        if (braceStart < 0) return [];

        int depth = 0;
        int funcEnd = lines.Length - 1;
        for (int i = braceStart; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth == 0) { funcEnd = i; break; }
        }

        var result = new List<LocalVariable>();
        var paramNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (funcMatch is { Success: true })
        {
            var rawParams = funcMatch.Groups["params"].Value;
            foreach (var part in rawParams.Split(','))
            {
                var paramName = part.Trim().TrimStart('&', '*').Trim();
                if (string.IsNullOrEmpty(paramName)) continue;

                int space = paramName.IndexOfAny([' ', '\t']);
                if (space > 0) paramName = paramName[..space];

                if (!System.Text.RegularExpressions.Regex.IsMatch(paramName, @"^[A-Za-z_]\w*$"))
                    continue;
                if (GscLanguageKeywords.LocalVariableReservedWords.Contains(paramName)) continue;

                if (paramNames.Add(paramName))
                    result.Add(new LocalVariable(paramName, "parameter", funcDefLine + 1));
            }
        }

        for (int i = braceStart; i <= funcEnd; i++)
        {
            var match = LocalVarAssignmentRegex().Match(lines[i]);
            if (!match.Success) continue;

            string name = match.Groups[1].Value;
            string value = match.Groups[2].Value.Trim();

            if (GscLanguageKeywords.LocalVariableReservedWords.Contains(name)) continue;

            result.Add(new LocalVariable(name, value, i + 1));
        }

        return result;
    }

    public static List<MacroDefinition> GetFileMacros(string filePath)
    {
        string cacheKey = filePath.Replace("\\", "/").ToLower();

        lock (_macroCacheLock)
        {
            if (_macroCache.TryGetValue(cacheKey, out var cached))
                return cached;
        }

        var result = ScanFileMacros(filePath);

        lock (_macroCacheLock)
        {
            _macroCache[cacheKey] = result;
        }

        return result;
    }

    private static List<MacroDefinition> ScanFileMacros(string filePath)
    {
        var macros = new List<MacroDefinition>();
        try
        {
            var lines = File.ReadAllLines(filePath);
            for (int i = 0; i < lines.Length; i++)
            {
                var trimmed = lines[i].TrimStart();
                if (!trimmed.StartsWith("#define ")) continue;

                var afterDefine = trimmed[8..];
                int sepIdx = afterDefine.IndexOfAny([' ', '\t', '(']);

                string macroName = sepIdx < 0 ? afterDefine.Trim() : afterDefine[..sepIdx].Trim();
                int valueStart = sepIdx < 0 ? afterDefine.Length
                               : afterDefine[sepIdx] == '(' ? sepIdx
                               : sepIdx + 1;
                string macroValue = afterDefine[valueStart..].Trim();

                if (!string.IsNullOrEmpty(macroName))
                {
                    macros.Add(new MacroDefinition(macroName, macroValue, filePath, i + 1));
                }
            }
        }
        catch { }
        return macros;
    }

    public MacroDefinition? ResolveMacro(string callingFilePath, string macroName)
    {
        var localMacros = GetFileMacros(callingFilePath);
        var found = localMacros.FirstOrDefault(m => m.Name.Equals(macroName, StringComparison.OrdinalIgnoreCase));
        if (found != null) return found;

        string normalizedPath = callingFilePath.Replace("\\", "/").ToLower();

        if (!_workspaceFileMaps.TryGetValue(normalizedPath, out GscFileMap? fileMap))
            _fileMaps.TryGetValue(normalizedPath, out fileMap);

        if (fileMap != null)
        {
            foreach (var inlinePath in fileMap.Inlines)
            {
                var resolvedPath = ResolveInlinePath(inlinePath);
                if (resolvedPath == null) continue;

                var inlineMacros = GetFileMacros(resolvedPath);
                found = inlineMacros.FirstOrDefault(m => m.Name.Equals(macroName, StringComparison.OrdinalIgnoreCase));
                if (found != null) return found;
            }
        }

        return null;
    }

    public List<MacroDefinition> GetAllVisibleMacros(string callingFilePath)
    {
        var allMacros = new List<MacroDefinition>();
        allMacros.AddRange(GetFileMacros(callingFilePath));

        string normalizedPath = callingFilePath.Replace("\\", "/").ToLower();

        if (!_workspaceFileMaps.TryGetValue(normalizedPath, out GscFileMap? fileMap))
            _fileMaps.TryGetValue(normalizedPath, out fileMap);

        if (fileMap != null)
        {
            foreach (var inlinePath in fileMap.Inlines)
            {
                var resolvedPath = ResolveInlinePath(inlinePath);
                if (resolvedPath == null) continue;
                allMacros.AddRange(GetFileMacros(resolvedPath));
            }
        }

        var inactiveByFile = new Dictionary<string, List<InactiveRange>>(StringComparer.OrdinalIgnoreCase);
        bool IsActive(MacroDefinition m)
        {
            if (!inactiveByFile.TryGetValue(m.FilePath, out var ranges))
            {
                ranges = GscInactiveRegionAnalyzer.Analyze(GetFileLines(m.FilePath), CurrentGame);
                inactiveByFile[m.FilePath] = ranges;
            }
            int z = m.Line - 1;
            return !ranges.Any(r => z >= r.StartLine && z <= r.EndLine);
        }

        var result = new List<MacroDefinition>();
        var nameToIndex = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in allMacros)
        {
            if (nameToIndex.TryGetValue(m.Name, out var idx))
            {
                if (!IsActive(result[idx]) && IsActive(m))
                    result[idx] = m;
            }
            else
            {
                nameToIndex[m.Name] = result.Count;
                result.Add(m);
            }
        }

        return result;
    }

    private string? ResolveInlinePath(string inlinePath)
    {
        string normalized = inlinePath.Replace("\\", "/").ToLower();
        if (!normalized.EndsWith(".gsh")) normalized += ".gsh";

        var match = _workspaceFileMaps.Values.FirstOrDefault(f =>
            f.FilePath.Replace("\\", "/").ToLower().EndsWith(normalized));
        if (match != null) return match.FilePath;

        match = _fileMaps.Values.FirstOrDefault(f =>
            f.FilePath.Replace("\\", "/").ToLower().EndsWith(normalized));
        if (match != null) return match.FilePath;

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

        var isMethodStyleCall = !string.IsNullOrWhiteSpace(parsed.Caller);
        return ResolveFunction(contextPath, lookupName, isMethodStyleCall);
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

    public IEnumerable<string> GetAllIndexedFilePaths() =>
        _workspaceFileMaps.Values.Select(f => f.FilePath)
            .Concat(_fileMaps.Values.Select(f => f.FilePath))
            .Distinct(StringComparer.OrdinalIgnoreCase);

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

    public void UpdateSettingDumpPath(string? settingDumpPath)
    {
        _settingDumpPath = settingDumpPath;
        ApplyConfiguredDumpPath();
    }

    private void ApplyConfiguredDumpPath()
    {
        var (hasWorkspaceConfig, workspaceConfig) = TryReadWorkspaceConfig(WorkspacePath);
        var resolvedDumpPath = ResolveDumpPathValue(_settingDumpPath, WorkspacePath, hasWorkspaceConfig, workspaceConfig?.DumpPath);

        if (hasWorkspaceConfig)
        {
            if (string.IsNullOrWhiteSpace(workspaceConfig?.DumpPath))
                Console.Error.WriteLine("GSCLSP: gsclsp.config.json found with no dumpPath. Clearing dump index.");
            else
                Console.Error.WriteLine($"GSCLSP: dumpPath from gsclsp.config.json -> {workspaceConfig.DumpPath}");
        }
        else
        {
            Console.Error.WriteLine("GSCLSP: gsclsp.config.json not found. Dump index disabled unless configured.");
        }

        LoadConfiguredBuiltIns(hasWorkspaceConfig, workspaceConfig?.Game);
        UpdateDumpPath(resolvedDumpPath);
    }

    private static string? ResolveDumpPathValue(string? settingDumpPath, string? workspacePath, bool hasWorkspaceConfig, string? workspaceDumpPath)
    {
        var candidate = hasWorkspaceConfig ? workspaceDumpPath : settingDumpPath;

        if (string.IsNullOrWhiteSpace(candidate))
            return null;

        var clean = candidate.Trim();
        if (Uri.TryCreate(clean, UriKind.Absolute, out var uri) && uri.IsFile)
            clean = uri.LocalPath;

        if (!Path.IsPathRooted(clean) && !string.IsNullOrEmpty(workspacePath))
            clean = Path.GetFullPath(Path.Combine(workspacePath, clean));

        return clean;
    }

    private static readonly TimeSpan GscToolCacheMaxAge = TimeSpan.FromDays(7);

    private void LoadConfiguredBuiltIns(bool hasWorkspaceConfig, string? workspaceGame)
    {
        var game = ResolveGameValue(hasWorkspaceConfig, workspaceGame);
        var normalizedGame = string.IsNullOrWhiteSpace(game)
            ? "iw4"
            : game.Trim().ToLowerInvariant();

        var gameChanged = !string.Equals(CurrentGame, normalizedGame, StringComparison.Ordinal);
        CurrentGame = normalizedGame;

        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        var dataPath = Path.Combine(basePath, "data");

        var requestedPath = Path.Combine(dataPath, $"{normalizedGame}_builtins.json");
        var fallbackPath = Path.Combine(dataPath, "iw4_builtins.json");

        if (File.Exists(requestedPath))
        {
            BuiltIns.LoadBuiltIns(requestedPath);
            Console.Error.WriteLine($"GSCLSP: loaded builtins for game '{normalizedGame}'.");
            if (gameChanged) GameChanged?.Invoke(normalizedGame);
            return;
        }

        if (GscToolBuiltInsLoader.IsSupported(normalizedGame))
        {
            var cached = GscToolBuiltInsLoader.TryLoadFromCache(normalizedGame);
            if (cached != null)
            {
                BuiltIns.LoadNameOnlyBuiltIns(cached.Functions, cached.Methods);
                Console.Error.WriteLine($"GSCLSP: Loaded cached builtins for game '{normalizedGame}'.");
                if (gameChanged) GameChanged?.Invoke(normalizedGame);

                if (GscToolBuiltInsLoader.IsCacheStale(normalizedGame, GscToolCacheMaxAge))
                    _ = RefreshGscToolBuiltInsAsync(normalizedGame);
            }
            else
            {
                BuiltIns.LoadNameOnlyBuiltIns([], []);
                Console.Error.WriteLine($"GSCLSP: No cache for '{normalizedGame}', fetching from gsc-tool...");
                if (gameChanged) GameChanged?.Invoke(normalizedGame);
                _ = RefreshGscToolBuiltInsAsync(normalizedGame);
            }
            return;
        }

        if (!normalizedGame.Equals("iw4", StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"GSCLSP: Builtins for game '{normalizedGame}' not found, using iw4_builtins.json instead...");
        }

        if (File.Exists(fallbackPath))
        {
            BuiltIns.LoadBuiltIns(fallbackPath);
            if (gameChanged) GameChanged?.Invoke(normalizedGame);
            return;
        }

        Console.Error.WriteLine("GSCLSP: No builtins file found, expected data/iw4_builtins.json");
        if (gameChanged) GameChanged?.Invoke(normalizedGame);
    }

    private async Task RefreshGscToolBuiltInsAsync(string game)
    {
        var fetched = await GscToolBuiltInsLoader.FetchAsync(game).ConfigureAwait(false);
        if (fetched == null)
        {
            await Console.Error.WriteLineAsync($"GSCLSP: gsc-tool fetch for '{game}' failed or unsupported.");
            return;
        }

        if (!string.Equals(CurrentGame, game, StringComparison.OrdinalIgnoreCase))
            return;

        var previousFunctions = BuiltIns.GetAll()
            .Where(symbol => symbol.Type == SymbolType.Function)
            .Select(symbol => symbol.Name)
            .ToArray();
        var previousMethods = BuiltIns.GetAll(preferMethodsFirst: true)
            .Where(symbol => symbol.Type == SymbolType.Method)
            .Select(symbol => symbol.Name)
            .ToArray();
        var builtInsChanged = !BuiltInNamesEqual(previousFunctions, fetched.Functions)
            || !BuiltInNamesEqual(previousMethods, fetched.Methods);

        BuiltIns.LoadNameOnlyBuiltIns(fetched.Functions, fetched.Methods);
        await Console.Error.WriteLineAsync($"GSCLSP: Refreshed gsc-tool built-ins for game '{game}'.");
        if (builtInsChanged)
            GameChanged?.Invoke(game);
    }

    private static bool BuiltInNamesEqual(IEnumerable<string> left, IEnumerable<string> right)
    {
        return new HashSet<string>(left, StringComparer.OrdinalIgnoreCase)
            .SetEquals(right);
    }

    private static string ResolveGameValue(bool hasWorkspaceConfig, string? workspaceGame)
    {
        if (hasWorkspaceConfig && !string.IsNullOrWhiteSpace(workspaceGame))
            return workspaceGame.Trim();

        return "iw4";
    }

    private sealed record WorkspaceConfig(string? DumpPath, string? Game);

    private static (bool HasConfigFile, WorkspaceConfig? Config) TryReadWorkspaceConfig(string? workspacePath)
    {
        if (string.IsNullOrWhiteSpace(workspacePath) || !Directory.Exists(workspacePath))
            return (false, null);

        var configPath = Path.Combine(workspacePath, "gsclsp.config.json");
        if (!File.Exists(configPath))
            return (false, null);

        return (true, ReadWorkspaceConfigFromFile(configPath));
    }

    private static WorkspaceConfig? ReadWorkspaceConfigFromFile(string filePath)
    {
        if (!File.Exists(filePath))
            return null;

        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(filePath));
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
                return null;

            string? dumpPath = null;
            string? game = null;

            if (root.TryGetProperty("dumpPath", out var directDumpPath) && directDumpPath.ValueKind == JsonValueKind.String)
                dumpPath = directDumpPath.GetString();

            if (root.TryGetProperty("game", out var gameProperty) && gameProperty.ValueKind == JsonValueKind.String)
                game = gameProperty.GetString();

            return new WorkspaceConfig(dumpPath, game);
        }
        catch { }

        return null;
    }
}
