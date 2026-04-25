using GSCLSP.Core.Indexing;
using GSCLSP.Core.Diagnostics;
using GSCLSP.Core.Models;
using Microsoft.Extensions.Logging;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

public class GscDiagnosticsHandler
{
    public const string UnresolvedFunctionDiagnosticCode = "gsclsp.unresolvedFunction";
    public const string RecursiveFunctionWarningCode = "gsclsp.recursiveFunction";
    public const string MissingSemicolonWarningCode = "gsclsp.missingSemicolon";
    public const string InvalidBuiltinArgCountDiagnosticCode = "gsclsp.invalidBuiltinArgCount";

    private readonly GscIndexer _indexer;
    private readonly GscDiagnosticsAnalyzer _diagnosticsAnalyzer;
    private readonly ILanguageServerFacade _languageServer;
    private readonly GscDocumentStore _documentStore;
    private readonly ILogger<GscDiagnosticsHandler> _logger;
    private CancellationTokenSource? _republishCancellationTokenSource;

    public GscDiagnosticsHandler(GscIndexer indexer, ILanguageServerFacade languageServer, GscDocumentStore documentStore, ILogger<GscDiagnosticsHandler> logger)
    {
        _indexer = indexer;
        _languageServer = languageServer;
        _documentStore = documentStore;
        _logger = logger;
        _diagnosticsAnalyzer = new GscDiagnosticsAnalyzer(indexer);

        _indexer.GameChanged += _ => StartRepublishAllOpenDocuments();
    }

    private void StartRepublishAllOpenDocuments()
    {
        var cancellationTokenSource = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _republishCancellationTokenSource, cancellationTokenSource);
        previous?.Cancel();

        _ = RepublishAllOpenDocumentsAsync(cancellationTokenSource);
    }

    private async Task RepublishAllOpenDocumentsAsync(CancellationTokenSource cancellationTokenSource)
    {
        var cancellationToken = cancellationTokenSource.Token;
        var tasks = _documentStore.OpenDocuments
            .Select(document => RepublishDocumentAsync(document.Key, document.Value, cancellationToken))
            .ToArray();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            cancellationTokenSource.Dispose();
        }
    }

    private async Task RepublishDocumentAsync(DocumentUri uri, string text, CancellationToken cancellationToken)
    {
        try
        {
            await PublishAsync(uri, text, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to republish diagnostics for '{Uri}'", uri);
        }
    }

    public async Task PublishAsync(DocumentUri uri, string text, CancellationToken cancellationToken)
    {
        var diagnostics = await CollectDiagnosticsAsync(uri.GetFileSystemPath(), text, cancellationToken);

        _languageServer.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(diagnostics)
        });

        await PublishInactiveRegionsAsync(uri, text, cancellationToken);
    }

    public async Task PublishInactiveRegionsAsync(DocumentUri uri, string text, CancellationToken cancellationToken)
    {
        var ranges = await Task.Run(() =>
        {
            var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var lexed = new GSCLSP.Lexer.GscLexer().Lex(text);
            var ifdefRanges = GscInactiveRegionAnalyzer.Analyze(lines, _indexer.CurrentGame);
            var deadCode = GscDeadCodeAnalyzer.Analyze(lines, lexed.Tokens);

            return ifdefRanges
                .Concat(deadCode.DeadRanges)
                .Select(r => new { start = r.StartLine, end = r.EndLine })
                .ToArray();
        }, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        _languageServer.SendNotification("custom/inactiveRegions", new
        {
            uri = uri.ToString(),
            ranges
        });
    }

    public Task ClearAsync(DocumentUri uri, CancellationToken cancellationToken)
    {
        _languageServer.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>()
        });

        return Task.CompletedTask;
    }

    public async Task<IReadOnlyList<string>> GetSuggestedIncludesAsync(string currentFilePath, string currentText, string functionName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(currentText)) return [];

        var lines = currentText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var includedFiles = await GetIncludedFilesAsync(lines, cancellationToken);
        var localFunctions = GetLocalFunctions(currentText);
        var suggestions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var symbol in FindCandidateSymbols(functionName))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (symbol.FilePath.Equals("Engine", StringComparison.OrdinalIgnoreCase))
                continue;

            if (localFunctions.Contains(symbol.Name))
                continue;

            if (PathMatches(currentFilePath, symbol.FilePath))
                continue;

            if (includedFiles.Any(f => PathMatches(f.Path, symbol.FilePath) && f.Functions.Contains(symbol.Name)))
                continue;

            var includePath = ToIncludePath(symbol.FilePath);
            if (!string.IsNullOrWhiteSpace(includePath))
                suggestions.Add(includePath);
        }

        return [.. suggestions.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)];
    }

    public async Task<List<Diagnostic>> CollectDiagnosticsAsync(string filePath, string text, CancellationToken cancellationToken)
    {
        return await _diagnosticsAnalyzer.CollectDiagnosticsAsync(filePath, text, cancellationToken);
    }

    private static HashSet<string> GetLocalFunctions(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = FunctionMultiLineRegex().Matches(text);

        foreach (Match match in matches)
        {
            if (match.Success)
            {
                result.Add(match.Groups["name"].Value);
            }
        }

        return result;
    }

    private async Task<List<IncludedFileScope>> GetIncludedFilesAsync(string[] lines, CancellationToken cancellationToken, bool[]? devBlockMask = null)
    {
        var result = new List<IncludedFileScope>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            cancellationToken.ThrowIfCancellationRequested();

            if (devBlockMask != null && lineIndex < devBlockMask.Length && devBlockMask[lineIndex])
                continue;

            if (!GscHandlerCommon.TryExtractDirectivePath(line, out var includePath, includeInline: false))
                continue;

            var resolvedPath = await _indexer.GetIncludePathAsync(includePath);
            if (string.IsNullOrEmpty(resolvedPath) || !seen.Add(resolvedPath))
                continue;

            var readablePath = ResolveReadablePath(resolvedPath);
            var content = _indexer.GetFileContent(readablePath);
            var functions = GetLocalFunctions(content);
            result.Add(new IncludedFileScope(resolvedPath, functions));
        }

        return result;
    }

    private IEnumerable<GscSymbol> FindCandidateSymbols(string functionName)
    {
        return _indexer.WorkspaceSymbols
            .Concat(_indexer.Symbols)
            .Where(s => s.Name.Equals(functionName, StringComparison.OrdinalIgnoreCase))
            .GroupBy(s => s.FilePath, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First());
    }

    private string? ToIncludePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath)) return null;

        var normalized = filePath.Replace("\\", "/");
        var roots = new[] { _indexer.WorkspacePath, _indexer.DumpPath }
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!.Replace("\\", "/").TrimEnd('/'));

        foreach (var root in roots)
        {
            if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[(root.Length + 1)..];
                break;
            }
        }

        if (normalized.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];

        return normalized.Replace("/", "\\");
    }

    private string ResolveReadablePath(string filePath)
    {
        if (Path.IsPathRooted(filePath))
            return filePath;

        if (!string.IsNullOrWhiteSpace(_indexer.DumpPath))
            return Path.Combine(_indexer.DumpPath, filePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar));

        return filePath;
    }

    private static bool PathMatches(string filePath, string scriptPath)
    {
        var normalizedFile = filePath.Replace("\\", "/").ToLowerInvariant();
        var normalizedScript = scriptPath.Replace("\\", "/").ToLowerInvariant();

        if (!normalizedScript.EndsWith(".gsc"))
            normalizedScript += ".gsc";

        return normalizedFile.EndsWith(normalizedScript, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record IncludedFileScope(string Path, HashSet<string> Functions);
    private sealed record FunctionDefinition(string Name, int DefinitionLine, int NameColumn, int BraceLine);
    private sealed record MuteConfig(HashSet<string> TopOfFileMutes, Dictionary<int, HashSet<string>> LineMutes);
}
