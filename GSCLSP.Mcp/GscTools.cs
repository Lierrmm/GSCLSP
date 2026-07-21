using System.ComponentModel;
using System.Text.Json;
using System.Text.RegularExpressions;
using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using ModelContextProtocol.Server;

namespace GSCLSP.Mcp;

[McpServerToolType]
public static class GscTools
{
    private const int MaxLimit = 200;
    private const int MaxScriptLines = 2000;
    private const int MaxScriptBytes = 100 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static string Json(object value) => JsonSerializer.Serialize(value, JsonOptions);

    private static int Clamp(int limit) => limit < 1 ? 1 : Math.Min(limit, MaxLimit);

    private static object Describe(GscSymbol s) => new
    {
        name = s.Name,
        type = s.Type.ToString(),
        filePath = s.FilePath,
        line = s.LineNumber,
        parameters = s.Parameters,
        documentation = string.IsNullOrEmpty(s.Documentation) ? null : s.Documentation,
        minArgs = s.MinArgs,
        maxArgs = s.MaxArgs,
        isVariadic = s.IsVariadic,
        isPrivate = s.IsPrivate,
        isBuiltIn = s.FilePath.Equals("Engine", StringComparison.OrdinalIgnoreCase)
    };

    [McpServerTool(Name = "get_status")]
    [Description("Report the current state of the GSC index: workspace path, active game, dump path, whether the dump is indexed and how many symbols it holds, workspace symbol count, builtin count, and whether indexing is still in progress. Call this FIRST when starting to work with a GSC project, or whenever other GSC tools return empty/unexpected results, to confirm the server is ready and pointed at the right workspace.")]
    public static string GetStatus(GscIndexerService service)
    {
        var indexer = service.Indexer;
        return Json(new
        {
            workspacePath = service.WorkspacePath,
            workspaceExists = Directory.Exists(service.WorkspacePath),
            currentGame = indexer.CurrentGame,
            dumpPath = indexer.DumpPath,
            dumpIndexed = indexer.SymbolCount > 0,
            dumpSymbolCount = indexer.SymbolCount,
            workspaceSymbolCount = indexer.WorkspaceSymbols.Count,
            builtinCount = indexer.BuiltIns.GetNames().Count(),
            indexingInProgress = service.IndexingInProgress,
            primer = "If you have not read the GSC primer this session, call get_gsc_primer (or read resource gsclsp://primer) before writing GSC code."
        });
    }

    [McpServerTool(Name = "search_symbols")]
    [Description("Search all indexed GSC symbols (dump library + the user's workspace) plus engine builtin names by substring match. Use this to find where a function is defined, discover functions by partial name, or check whether a symbol exists anywhere in the project. Returns name, type, file path, line, and parameters for each match. Prefer this over guessing function locations.")]
    public static string SearchSymbols(
        GscIndexerService service,
        [Description("Substring to match against symbol names (case-insensitive).")] string query,
        [Description("Maximum results to return (default 50, capped at 200).")] int limit = 50)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        int cap = Clamp(limit);
        var indexer = service.Indexer;

        var symbolMatches = indexer.WorkspaceSymbols
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Concat(indexer.Search(query));

        var builtinMatches = indexer.BuiltIns.GetAll()
            .Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        var all = symbolMatches.Concat(builtinMatches)
            .DistinctBy(s => (s.Name, s.FilePath, s.LineNumber))
            .ToList();

        var page = all.Take(cap).Select(Describe).ToList();

        return Json(new
        {
            query,
            totalMatches = all.Count,
            returned = page.Count,
            truncated = all.Count > page.Count,
            results = page
        });
    }

    [McpServerTool(Name = "get_symbol")]
    [Description("Get every definition that shares an exact function/method name: workspace definitions, dump-library definitions, and engine builtins (both the function and method variant when both exist). Use this when you know the exact name and need full details — signature, documentation, min/max argument counts, and the defining file and line. Call it to understand how to invoke a function or to see overloads across the dump.")]
    public static string GetSymbol(
        GscIndexerService service,
        [Description("Exact symbol name (case-insensitive).")] string name)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        var indexer = service.Indexer;

        var results = new List<GscSymbol>();
        results.AddRange(indexer.WorkspaceSymbols.Where(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)));
        results.AddRange(indexer.GetSymbolsByName(name));

        var builtinFunction = indexer.BuiltIns.GetBuiltIn(name, preferMethod: false);
        if (builtinFunction != null) results.Add(builtinFunction);
        var builtinMethod = indexer.BuiltIns.GetBuiltIn(name, preferMethod: true);
        if (builtinMethod != null && !ReferenceEquals(builtinMethod, builtinFunction)) results.Add(builtinMethod);

        var distinct = results
            .DistinctBy(s => (s.Name, s.FilePath, s.LineNumber, s.Type))
            .Select(Describe)
            .ToList();

        return Json(new
        {
            name,
            found = distinct.Count > 0,
            definitions = distinct
        });
    }

    [McpServerTool(Name = "list_builtins")]
    [Description("List engine builtin function/method names available for the current target game (e.g. iprintln, getentarray, spawn). Optionally filter by substring. Use this to discover engine-provided functions, which do NOT live in any source file. Call get_symbol for a specific builtin's signature and argument counts.")]
    public static string ListBuiltins(
        GscIndexerService service,
        [Description("Optional substring filter (case-insensitive). Omit to list all.")] string? filter = null,
        [Description("Maximum names to return (default 100, capped at 200).")] int limit = 100)
    {
        int cap = Clamp(limit);
        var indexer = service.Indexer;

        var names = indexer.BuiltIns.GetNames();
        if (!string.IsNullOrEmpty(filter))
            names = names.Where(n => n.Contains(filter, StringComparison.OrdinalIgnoreCase));

        var ordered = names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList();
        var page = ordered.Take(cap).ToList();

        return Json(new
        {
            game = indexer.CurrentGame,
            filter,
            totalMatches = ordered.Count,
            returned = page.Count,
            truncated = ordered.Count > page.Count,
            names = page
        });
    }

    [McpServerTool(Name = "list_script_files")]
    [Description("List indexed GSC script file paths from the dump library and workspace, optionally filtered by a path prefix/substring. Use this to explore the layout of the game's script dump or to find the file that owns a given path (e.g. 'maps/mp/gametypes'). Follow up with get_functions_in_file or read_script on a returned path.")]
    public static string ListScriptFiles(
        GscIndexerService service,
        [Description("Optional path substring filter (case-insensitive), e.g. 'maps/mp'. Omit to list all.")] string? pathPrefix = null,
        [Description("Maximum paths to return (default 100, capped at 200).")] int limit = 100)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        int cap = Clamp(limit);
        var indexer = service.Indexer;

        var paths = indexer.GetAllIndexedFilePaths();
        if (!string.IsNullOrEmpty(pathPrefix))
        {
            string needle = pathPrefix.Replace("\\", "/");
            paths = paths.Where(p => p.Replace("\\", "/").Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var ordered = paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToList();
        var page = ordered.Take(cap).ToList();

        return Json(new
        {
            pathPrefix,
            totalMatches = ordered.Count,
            returned = page.Count,
            truncated = ordered.Count > page.Count,
            files = page
        });
    }

    [McpServerTool(Name = "get_functions_in_file")]
    [Description("List all functions/methods defined in a single GSC script, identified by a script path or partial path (e.g. 'gametypes/_globallogic' or a full dump path). Use this to understand a file's public API before reading its source or calling into it. Returns each function's name, line, and signature.")]
    public static string GetFunctionsInFile(
        GscIndexerService service,
        [Description("Script path or partial path, e.g. 'maps/mp/_utility' or 'common_scripts/utility'.")] string scriptPath)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        var symbols = service.Indexer.GetSymbolsByPath(scriptPath)
            .DistinctBy(s => (s.Name, s.FilePath, s.LineNumber))
            .Select(Describe)
            .ToList();

        return Json(new
        {
            scriptPath,
            count = symbols.Count,
            functions = symbols
        });
    }

    [McpServerTool(Name = "read_script")]
    [Description("Read the source of a GSC script from the dump library or workspace, returned with line numbers. Provide an include-style path (e.g. 'maps/mp/_utility') or a full indexed path. Use this to inspect an implementation after locating it with search_symbols or get_functions_in_file. Output is capped at 2000 lines / 100 KB; pass startLine/endLine to read a slice of large files.")]
    public static string ReadScript(
        GscIndexerService service,
        [Description("Script path: an include path like 'maps/mp/_utility' or a full indexed file path.")] string scriptPath,
        [Description("First line to return (1-based, default 1).")] int startLine = 1,
        [Description("Last line to return (1-based, inclusive). Omit to read to end (subject to the line cap).")] int? endLine = null)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        var indexer = service.Indexer;

        string? resolved = ResolveScriptPath(indexer, scriptPath);
        if (resolved == null)
            return Json(new { scriptPath, error = "not_found", message = "Could not resolve the script path to an indexed dump or workspace file." });

        string full;
        try
        {
            full = Path.GetFullPath(resolved);
        }
        catch
        {
            return Json(new { scriptPath, error = "invalid_path" });
        }

        if (!IsWithinAllowedRoots(indexer, full))
            return Json(new { scriptPath, error = "access_denied", message = "Resolved path is outside the dump and workspace roots." });

        if (!File.Exists(full))
            return Json(new { scriptPath, error = "not_found", message = "Resolved file does not exist on disk." });

        var lines = indexer.GetFileLines(full);
        int start = Math.Max(1, startLine);
        int end = endLine.HasValue ? Math.Min(lines.Length, endLine.Value) : lines.Length;
        if (end - start + 1 > MaxScriptLines)
            end = start + MaxScriptLines - 1;

        var sb = new System.Text.StringBuilder();
        bool truncatedByBytes = false;
        int lastLine = start - 1;
        for (int i = start; i <= end && i <= lines.Length; i++)
        {
            string entry = $"{i,6}  {lines[i - 1]}\n";
            if (sb.Length + entry.Length > MaxScriptBytes)
            {
                truncatedByBytes = true;
                break;
            }
            sb.Append(entry);
            lastLine = i;
        }

        return Json(new
        {
            scriptPath,
            resolvedPath = full,
            totalLines = lines.Length,
            startLine = start,
            endLine = lastLine,
            truncated = lastLine < lines.Length || truncatedByBytes,
            content = sb.ToString()
        });
    }

    [McpServerTool(Name = "search_script_content")]
    [Description("Grep script bodies (dump + workspace) for a substring or regex: string literals, hashed names (_id_XXXX), code patterns. search_symbols matches names only; this searches text. Returns file, line, snippet.")]
    public static string SearchScriptContent(
        GscIndexerService service,
        [Description("Text to find. Plain substring by default (case-insensitive). Set isRegex=true for .NET regex syntax.")] string pattern,
        [Description("Treat pattern as a case-insensitive .NET regular expression (default false = plain substring).")] bool isRegex = false,
        [Description("Optional file-path substring filter, e.g. 'gametypes' or 'scripts/mp'. Omit to search everything.")] string? pathFilter = null,
        [Description("Maximum matches to return (default 30, capped at 100).")] int maxResults = 30,
        [Description("Context lines before/after each match (default 1, max 4).")] int contextLines = 1)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        if (string.IsNullOrEmpty(pattern))
            return Json(new { error = "empty_pattern", message = "Provide a non-empty search pattern." });

        int cap = Math.Clamp(maxResults, 1, 100);
        int context = Math.Clamp(contextLines, 0, 4);

        Regex? regex = null;
        if (isRegex)
        {
            try
            {
                regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
            }
            catch (ArgumentException ex)
            {
                return Json(new { error = "invalid_regex", message = ex.Message });
            }
        }

        var paths = service.Indexer.GetAllIndexedFilePaths();
        if (!string.IsNullOrEmpty(pathFilter))
        {
            string needle = pathFilter.Replace("\\", "/");
            paths = paths.Where(p => p.Replace("\\", "/").Contains(needle, StringComparison.OrdinalIgnoreCase));
        }

        var matches = new List<object>();
        int filesScanned = 0, filesMatched = 0;
        bool truncated = false;

        foreach (var path in paths)
        {
            string[] lines;
            try { lines = File.ReadAllLines(path); }
            catch { continue; }
            filesScanned++;

            bool fileHit = false;
            for (int i = 0; i < lines.Length; i++)
            {
                bool hit;
                try
                {
                    hit = regex != null
                        ? regex.IsMatch(lines[i])
                        : lines[i].Contains(pattern, StringComparison.OrdinalIgnoreCase);
                }
                catch (RegexMatchTimeoutException)
                {
                    return Json(new { error = "regex_timeout", message = $"Regex matching timed out on {path}:{i + 1}. Simplify the pattern." });
                }
                if (!hit) continue;

                fileHit = true;
                if (matches.Count >= cap)
                {
                    truncated = true;
                    break;
                }

                var sb = new System.Text.StringBuilder();
                int from = Math.Max(0, i - context);
                int to = Math.Min(lines.Length - 1, i + context);
                for (int j = from; j <= to; j++)
                {
                    string text = lines[j].Length > 300 ? lines[j][..300] + "…" : lines[j];
                    sb.Append($"{j + 1,6}  {text}\n");
                }

                matches.Add(new { file = path, line = i + 1, snippet = sb.ToString() });
            }

            if (fileHit) filesMatched++;
            if (truncated) break;
        }

        return Json(new
        {
            pattern,
            isRegex,
            pathFilter,
            filesScanned,
            filesMatched,
            returned = matches.Count,
            truncated,
            matches
        });
    }

    [McpServerTool(Name = "resolve_function")]
    [Description("Resolve how a function call would bind from a given calling script: it checks the local file, engine builtins, qualified 'path::name' targets, and #include'd files, in the same order the language server uses. Use this to answer 'where does foo() go when called from this file?' or to diagnose an unresolved-function problem. Returns the resolution type (Local/Included/Global/NotFound), the source file, and the target symbol details.")]
    public static string ResolveFunction(
        GscIndexerService service,
        [Description("Path of the file making the call (workspace or dump path, include-style paths accepted).")] string callingScriptPath,
        [Description("Function name being called, optionally qualified like 'path::name'.")] string functionName)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        var indexer = service.Indexer;
        string callingPath = ResolveScriptPath(indexer, callingScriptPath) ?? callingScriptPath;

        var resolution = indexer.ResolveFunction(callingPath, functionName);

        return Json(new
        {
            callingScriptPath,
            resolvedCallingPath = callingPath,
            functionName,
            resolutionType = resolution.Type.ToString(),
            resolved = resolution.Type != ResolutionType.NotFound,
            sourceFile = resolution.SourceFile,
            target = resolution.Symbol == null ? null : Describe(resolution.Symbol)
        });
    }

    [McpServerTool(Name = "get_problems")]
    [Description("Run the language server's diagnostics over the workspace and return current problems: unresolved functions, missing semicolons, builtin argument-count errors, recursion and early-return warnings. This is the same analysis that produces red underlines in the editor. Optionally filter to files whose path contains a substring. Use it to verify workspace GSC code is clean after edits, or to list what needs fixing.")]
    public static async Task<string> GetProblemsAsync(
        GscIndexerService service,
        [Description("Optional file-path substring filter (case-insensitive), e.g. 'survival' or 'custom_scripts'. Omit to check every workspace file.")] string? pathFilter = null,
        [Description("Maximum problems to return (default 100, capped at 200).")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        if (service.IndexingInProgress)
            return Json(new { status = "indexing_in_progress", message = "The GSC index is still building. Retry shortly or call get_status." });

        int cap = Clamp(limit);
        var indexer = service.Indexer;

        var files = indexer.WorkspaceScriptFiles;
        if (!string.IsNullOrEmpty(pathFilter))
        {
            string needle = pathFilter.Replace("\\", "/");
            files = [.. files.Where(p => p.Replace("\\", "/").Contains(needle, StringComparison.OrdinalIgnoreCase))];
        }

        var problems = new List<object>();
        int totalProblems = 0, filesWithProblems = 0;

        foreach (var filePath in files.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var text = indexer.GetFileContent(filePath);
            if (string.IsNullOrEmpty(text)) continue;

            List<OmniSharp.Extensions.LanguageServer.Protocol.Models.Diagnostic> diagnostics;
            try
            {
                diagnostics = await service.Diagnostics.CollectDiagnosticsAsync(filePath, text, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                continue;
            }

            if (diagnostics.Count == 0) continue;

            filesWithProblems++;
            totalProblems += diagnostics.Count;

            foreach (var d in diagnostics)
            {
                if (problems.Count >= cap) continue;

                string? code = null;
                if (d.Code is { } dc) code = dc.IsString ? dc.String : dc.Long.ToString();

                problems.Add(new
                {
                    file = filePath,
                    line = d.Range.Start.Line + 1,
                    character = d.Range.Start.Character + 1,
                    severity = d.Severity?.ToString(),
                    code,
                    message = d.Message
                });
            }
        }

        return Json(new
        {
            pathFilter,
            filesChecked = files.Count,
            filesWithProblems,
            totalProblems,
            returned = problems.Count,
            truncated = totalProblems > problems.Count,
            problems
        });
    }

    private static string? ResolveScriptPath(GscIndexer indexer, string scriptPath)
    {
        string? viaInclude = indexer.GetIncludePath(scriptPath);
        if (viaInclude != null) return viaInclude;

        if (Path.IsPathRooted(scriptPath) && File.Exists(scriptPath))
            return scriptPath;

        foreach (var root in AllowedRoots(indexer))
        {
            string candidate = Path.Combine(root, scriptPath);
            if (File.Exists(candidate)) return candidate;
        }

        var byPath = indexer.GetSymbolsByPath(scriptPath).FirstOrDefault();
        return byPath?.FilePath;
    }

    private static IEnumerable<string> AllowedRoots(GscIndexer indexer)
    {
        if (!string.IsNullOrEmpty(indexer.DumpPath)) yield return indexer.DumpPath;
        if (!string.IsNullOrEmpty(indexer.WorkspacePath)) yield return indexer.WorkspacePath;
    }

    private static bool IsWithinAllowedRoots(GscIndexer indexer, string fullPath)
    {
        foreach (var root in AllowedRoots(indexer))
        {
            string rootFull;
            try { rootFull = Path.GetFullPath(root); }
            catch { continue; }

            string prefix = rootFull.EndsWith(Path.DirectorySeparatorChar) ? rootFull : rootFull + Path.DirectorySeparatorChar;
            if (fullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                fullPath.Equals(rootFull, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
