using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

public partial class GscDiagnosticsHandler(GscIndexer indexer, ILanguageServerFacade languageServer)
{
    public const string UnresolvedFunctionDiagnosticCode = "gsclsp.unresolvedFunction";

    private static readonly HashSet<string> ReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "foreach", "while", "switch", "return", "wait",
        "waittill", "waittillmatch", "waittillframeend", "notify", "endon",
        "thread", "childthread", "break", "continue", "case", "default"
    };

    private readonly GscIndexer _indexer = indexer;
    private readonly ILanguageServerFacade _languageServer = languageServer;

    public async Task PublishAsync(DocumentUri uri, string text, CancellationToken cancellationToken)
    {
        var diagnostics = await CollectDiagnosticsAsync(uri.GetFileSystemPath(), text, cancellationToken);

        _languageServer.SendNotification("textDocument/publishDiagnostics", new PublishDiagnosticsParams
        {
            Uri = uri,
            Diagnostics = new Container<Diagnostic>(diagnostics)
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

    internal async Task<List<Diagnostic>> CollectDiagnosticsAsync(string filePath, string text, CancellationToken cancellationToken)
    {
        var diagnostics = new List<Diagnostic>();
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var localFunctions = GetLocalFunctions(text);
        var includedFiles = await GetIncludedFilesAsync(lines, cancellationToken);

        bool inBlockComment = false;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = lines[lineIndex];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                GscHandlerCommon.GetCodeRanges(line, ref inBlockComment);
                continue;
            }

            var codeRanges = GscHandlerCommon.GetCodeRanges(line, ref inBlockComment);
            if (codeRanges.Count == 0) continue;

            foreach (var (start, end) in codeRanges)
            {
                var segment = line[start..end];
                var matches = CallSiteRegex().Matches(segment);

                foreach (Match match in matches)
                {
                    var functionGroup = match.Groups["name"];
                    var functionName = functionGroup.Value;
                    if (string.IsNullOrEmpty(functionName) || ReservedWords.Contains(functionName))
                        continue;

                    if (IsFunctionDefinition(lines, lineIndex, start + match.Index, functionName))
                        continue;

                    if (IsVisibleFunction(filePath, functionName, match.Groups["path"].Value, localFunctions, includedFiles))
                        continue;

                    diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Error,
                        Source = "gsclsp",
                        Code = UnresolvedFunctionDiagnosticCode,
                        Data = functionName,
                        Message = $"Function '{functionName}' is not defined in this file or its included files.",
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(lineIndex, start + functionGroup.Index),
                            new Position(lineIndex, start + functionGroup.Index + functionGroup.Length))
                    });
                }
            }
        }

        return diagnostics;
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

    private async Task<List<IncludedFileScope>> GetIncludedFilesAsync(string[] lines, CancellationToken cancellationToken)
    {
        var result = new List<IncludedFileScope>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var line in lines)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#include") && !trimmed.StartsWith("#using"))
                continue;

            var match = DirectivePathRegex().Match(trimmed);
            if (!match.Success) continue;

            var includePath = match.Groups[1].Value.Trim();
            var resolvedPath = await _indexer.GetIncludePath(includePath);
            if (string.IsNullOrEmpty(resolvedPath) || !seen.Add(resolvedPath))
                continue;

            var readablePath = ResolveReadablePath(resolvedPath);
            var content = _indexer.GetFileContent(readablePath);
            var functions = GetLocalFunctions(content);
            result.Add(new IncludedFileScope(resolvedPath, functions));
        }

        return result;
    }

    private bool IsVisibleFunction(
        string currentFilePath,
        string functionName,
        string qualifiedPath,
        HashSet<string> localFunctions,
        List<IncludedFileScope> includedFiles)
    {
        if (localFunctions.Contains(functionName))
            return true;

        if (_indexer.BuiltIns.GetBuiltIn(functionName) != null)
            return true;

        if (!string.IsNullOrWhiteSpace(qualifiedPath))
        {
            if (PathMatches(currentFilePath, qualifiedPath) && localFunctions.Contains(functionName))
                return true;

            return FindCandidateSymbols(functionName)
                .Any(s => PathMatches(s.FilePath, qualifiedPath));
        }

        return includedFiles.Any(f => f.Functions.Contains(functionName));
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

    private static bool IsFunctionDefinition(string[] lines, int lineIndex, int matchIndex, string functionName)
    {
        var line = lines[lineIndex];
        if (line.Length == 0 || char.IsWhiteSpace(line[0])) return false;
        if (matchIndex != 0) return false;
        if (line.Contains(';')) return false;

        var match = FunctionMultiLineRegex().Match(line);
        if (!match.Success || !match.Groups["name"].Value.Equals(functionName, StringComparison.OrdinalIgnoreCase))
            return false;

        return line.Contains('{') || (lineIndex + 1 < lines.Length && lines[lineIndex + 1].TrimStart().StartsWith('{'));
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
}