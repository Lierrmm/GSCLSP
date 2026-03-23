using GSCLSP.Core.Models;
using System.Diagnostics;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Indexing;

public partial class GscIndexer
{
    public void IndexWorkspace(string workspacePath)
    {
        Console.Error.WriteLine($"Indexing Workspace {workspacePath}");
        var localSymbols = new List<GscSymbol>();

        if (!Directory.Exists(workspacePath)) return;

        _workspaceFileMaps.Clear();
        _fileContentCache.Clear();
        WorkspacePath = workspacePath;

        var files = Directory.GetFiles(workspacePath, "*.*", SearchOption.AllDirectories)
            .Where(IsScriptFile);

        foreach (var file in files)
        {
            string normalizedPath = NormalizePathKey(file);
            var parsed = ParseWorkspaceFileForIncrementalIndex(file);

            localSymbols.AddRange(parsed.Symbols);
            _workspaceFileMaps[normalizedPath] = parsed.FileMap;
        }

        WorkspaceSymbols = localSymbols;

        StartFileWatching();
        ApplyConfiguredDumpPath();

        PreWarmScanCache(localSymbols);
    }

    private static void PreWarmScanCache(List<GscSymbol> symbols)
    {
        Console.Error.WriteLine($"GSCLSP: Pre-warming scan cache for {symbols.Count} functions...");
        var sw = Stopwatch.StartNew();

        foreach (var symbol in symbols)
        {
            if (!symbol.FilePath.Equals("Engine", StringComparison.OrdinalIgnoreCase) &&
                symbol.Type == SymbolType.Function)
            {
                _ = ScanFileForFunction(symbol.FilePath, symbol.Name);
            }
        }

        sw.Stop();
        Console.Error.WriteLine($"GSCLSP: Scan cache pre-warmed in {sw.Elapsed.TotalMilliseconds:N2}ms");
    }

    public string GetFileContent(string filePath)
    {
        if (_fileContentCache.TryGetValue(filePath, out var cached))
            return cached;

        try
        {
            string content = File.ReadAllText(filePath);
            _fileContentCache[filePath] = content;
            return content;
        }
        catch
        {
            return string.Empty;
        }
    }

    public string[] GetFileLines(string filePath)
    {
        return GetFileContent(filePath).Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
    }

    private void StartFileWatching()
    {
        if (string.IsNullOrEmpty(WorkspacePath) || _fileWatcher != null)
            return;

        try
        {
            _fileWatcher = new FileSystemWatcher(WorkspacePath)
            {
                Filter = "*.*",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
                IncludeSubdirectories = true,
                EnableRaisingEvents = true
            };

            _fileWatcher.Changed += OnFileChanged;
            _fileWatcher.Created += OnFileChanged;
            _fileWatcher.Deleted += OnFileChanged;
            _fileWatcher.Renamed += OnFileRenamed;

            Console.Error.WriteLine($"GSCLSP: File watching started for {WorkspacePath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"GSCLSP: File watching failed: {ex.Message}");
        }
    }

    private static bool IsWorkspaceConfigFile(string fullPath) =>
        Path.GetFileName(fullPath).Equals("gsclsp.config.json", StringComparison.OrdinalIgnoreCase);

    private void ScheduleConfigReload()
    {
        _configDebounceTimer?.Stop();
        _configDebounceTimer?.Dispose();

        _configDebounceTimer = new System.Timers.Timer(DEBOUNCE_MS)
        {
            AutoReset = false,
        };

        _configDebounceTimer.Elapsed += (_, _) =>
        {
            try
            {
                ApplyConfiguredDumpPath();
            }
            catch { }
        };

        _configDebounceTimer.Start();
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (IsWorkspaceConfigFile(e.FullPath))
        {
            ScheduleConfigReload();
            return;
        }

        if (!IsScriptFile(e.FullPath))
            return;

        lock (_pendingChangesLock)
        {
            _pendingChanges.Add(e.FullPath);
        }

        _debounceTimer?.Stop();
        _debounceTimer = new System.Timers.Timer(DEBOUNCE_MS);
        _debounceTimer.Elapsed += (s, args) => ProcessPendingChanges();
        _debounceTimer.AutoReset = false;
        _debounceTimer.Start();
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        if (IsWorkspaceConfigFile(e.OldFullPath) || IsWorkspaceConfigFile(e.FullPath))
        {
            ScheduleConfigReload();
        }

        _fileContentCache.Remove(e.OldFullPath);
        OnFileChanged(sender, new FileSystemEventArgs(WatcherChangeTypes.Changed, Path.GetDirectoryName(e.FullPath)!, Path.GetFileName(e.FullPath)));
    }

    private void ProcessPendingChanges()
    {
        HashSet<string> changes;
        lock (_pendingChangesLock)
        {
            if (_pendingChanges.Count == 0)
                return;

            changes = [.. _pendingChanges];
            _pendingChanges.Clear();
        }

        Console.Error.WriteLine($"GSCLSP: Updating {changes.Count} files");

        var updatedSymbols = new List<GscSymbol>(WorkspaceSymbols);

        foreach (var filePath in changes)
        {
            InvalidateFileCaches(filePath);

            updatedSymbols.RemoveAll(s => s.FilePath.Equals(filePath, StringComparison.OrdinalIgnoreCase));

            if (File.Exists(filePath))
            {
                string normalizedPath = NormalizePathKey(filePath);

                try
                {
                    var parsed = ParseWorkspaceFileForIncrementalIndex(filePath);
                    updatedSymbols.AddRange(parsed.Symbols);
                    _workspaceFileMaps[normalizedPath] = parsed.FileMap;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"GSCLSP: Error re-parsing {filePath}: {ex.Message}");
                }
            }
            else
            {
                string normalizedPath = NormalizePathKey(filePath);
                _workspaceFileMaps.Remove(normalizedPath);
            }
        }

        WorkspaceSymbols = updatedSymbols;
        Console.Error.WriteLine($"GSCLSP: Workspace updated. {WorkspaceSymbols.Count} symbols now indexed.");
    }

    private static int GetLineNumberFromIndex(string content, int index)
    {
        int lineCount = 0;
        for (int i = 0; i < index; i++)
        {
            if (content[i] == '\n') lineCount++;
        }
        return lineCount + 1;
    }

    private static bool IsScriptFile(string path) =>
        path.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) ||
        path.EndsWith(".gsh", StringComparison.OrdinalIgnoreCase);

    private static string NormalizePathKey(string path) =>
        path.Replace("\\", "/").ToLowerInvariant();

    private void ClearGlobalIndexAndCaches()
    {
        _symbols.Clear();
        _fileMaps.Clear();

        lock (_scanCacheLock)
        {
            _scanFunctionCache.Clear();
        }

        lock (_localVarCacheLock)
        {
            _localVarCache.Clear();
        }

        lock (_macroCacheLock)
        {
            _macroCache.Clear();
        }
    }

    private void InvalidateFileCaches(string filePath)
    {
        _fileContentCache.Remove(filePath);

        lock (_scanCacheLock)
        {
            var keysToRemove = _scanFunctionCache.Keys
                .Where(k => k.StartsWith(filePath + "|", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
                _scanFunctionCache.Remove(key);
        }

        lock (_localVarCacheLock)
        {
            var keysToRemove = _localVarCache.Keys
                .Where(k => k.StartsWith(filePath + "|", StringComparison.OrdinalIgnoreCase))
                .ToList();

            foreach (var key in keysToRemove)
                _localVarCache.Remove(key);
        }

        lock (_macroCacheLock)
        {
            _macroCache.Remove(NormalizePathKey(filePath));
        }
    }

    private static (GscFileMap FileMap, List<GscSymbol> Symbols) ParseWorkspaceFileForIncrementalIndex(string filePath)
    {
        var fileMap = new GscFileMap { FilePath = filePath };
        var symbols = new List<GscSymbol>();
        var lines = File.ReadAllLines(filePath);

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            int lineNum = lineIndex + 1;

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

            var symbol = new GscSymbol(
                funcMatch.Groups["name"].Value,
                filePath,
                lineNum,
                CleanGscParams(funcMatch.Groups["params"].Value),
                SymbolType.Function
            );

            symbols.Add(symbol);
            fileMap.LocalSymbols.Add(symbol);
        }

        return (fileMap, symbols);
    }
}
