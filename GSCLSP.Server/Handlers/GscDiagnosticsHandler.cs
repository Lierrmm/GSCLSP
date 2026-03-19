using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using GSCLSP.Lexer;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

public partial class GscDiagnosticsHandler(GscIndexer indexer, ILanguageServerFacade languageServer)
{
    public const string UnresolvedFunctionDiagnosticCode = "gsclsp.unresolvedFunction";
    public const string RecursiveFunctionWarningCode = "gsclsp.recursiveFunction";
    public const string MissingSemicolonWarningCode = "gsclsp.missingSemicolon";

    private const string RecursiveWarningMuteKey = "recursive-function";
    private const string MissingSemicolonMuteKey = "missing-semicolon";

    private static readonly HashSet<string> ReservedWords = GscLanguageKeywords.DiagnosticReservedWords;

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
        var muteConfig = ParseMuteConfig(lines);
        var devBlockMask = BuildDevBlockMask(lines);

        var localFunctions = GetLocalFunctions(text);
        var includedFiles = await GetIncludedFilesAsync(lines, cancellationToken, devBlockMask);

        var lexer = new GscLexer();
        var lexed = lexer.Lex(text);

        var tokensByLine = lexed.Tokens
            .Where(IsSignificantToken)
            .GroupBy(t => t.Line)
            .ToDictionary(g => g.Key, g => g.ToList());

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (devBlockMask[lineIndex])
                continue;

            if (!tokensByLine.TryGetValue(lineIndex, out var lineTokens) || lineTokens.Count == 0)
                continue;

            for (int i = 0; i < lineTokens.Count - 1; i++)
            {
                var functionToken = lineTokens[i];
                if (!IsNameToken(functionToken) || lineTokens[i + 1].Kind != TokenKind.OpenParen)
                    continue;

                var functionName = functionToken.Text;
                if (string.IsNullOrEmpty(functionName) || ReservedWords.Contains(functionName))
                    continue;

                if (IsFunctionDefinition(lines, lineIndex, functionToken.Column, functionName))
                    continue;

                var qualifiedPath = TryGetQualifiedPath(lineTokens, i);
                if (!IsVisibleFunction(filePath, functionName, qualifiedPath, localFunctions, includedFiles))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Error,
                        Source = "gsclsp",
                        Code = UnresolvedFunctionDiagnosticCode,
                        Data = functionName,
                        Message = $"Function '{functionName}' is not defined in this file or its included files.",
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(lineIndex, functionToken.Column),
                            new Position(lineIndex, functionToken.Column + functionToken.Length))
                    });
                }
            }

            if (ShouldWarnForMissingSemicolon(lines, lineIndex, lineTokens) &&
                !IsMuted(muteConfig, MissingSemicolonMuteKey, lineIndex))
            {
                var (startColumn, endColumn) = GetLineContentRange(lines[lineIndex]);
                diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Error,
                    Source = "gsclsp",
                    Code = MissingSemicolonWarningCode,
                    Message = "Line should end with ';'.",
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(lineIndex, startColumn),
                        new Position(lineIndex, endColumn))
                });
            }
        }

        diagnostics.AddRange(CollectRecursiveFunctionWarnings(lines, tokensByLine, muteConfig, devBlockMask));

        return diagnostics;
    }

    private static List<Diagnostic> CollectRecursiveFunctionWarnings(
        string[] lines,
        IReadOnlyDictionary<int, List<Token>> tokensByLine,
        MuteConfig muteConfig,
        bool[] devBlockMask)
    {
        var diagnostics = new List<Diagnostic>();

        foreach (var function in GetFunctionDefinitions(lines))
        {
            if (IsMuted(muteConfig, RecursiveWarningMuteKey, function.DefinitionLine))
                continue;

            var bodyStart = function.BraceLine;
            var bodyEnd = FindFunctionBodyEndLine(lines, function.BraceLine);
            if (bodyEnd < bodyStart)
                continue;

            var hasRecursiveCall = false;

            for (int line = bodyStart; line <= bodyEnd; line++)
            {
                if (line >= 0 && line < devBlockMask.Length && devBlockMask[line])
                    continue;

                if (!tokensByLine.TryGetValue(line, out var lineTokens) || lineTokens.Count < 2)
                    continue;

                for (int i = 0; i < lineTokens.Count - 1; i++)
                {
                    if (lineTokens[i].Kind is TokenKind.Identifier or TokenKind.Keyword &&
                        lineTokens[i].Text.Equals(function.Name, StringComparison.OrdinalIgnoreCase) &&
                        lineTokens[i + 1].Kind == TokenKind.OpenParen)
                    {
                        // Ignore qualified calls like path::function(); they are not self-recursion.
                        if (i > 0 && lineTokens[i - 1].Kind == TokenKind.DoubleColon)
                            continue;

                        hasRecursiveCall = true;
                        break;
                    }
                }

                if (hasRecursiveCall)
                    break;
            }

            if (!hasRecursiveCall)
                continue;

            diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Source = "gsclsp",
                Code = RecursiveFunctionWarningCode,
                Data = function.Name,
                Message = $"Function '{function.Name}' is recursive.",
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(function.DefinitionLine, function.NameColumn),
                    new Position(function.DefinitionLine, function.NameColumn + function.Name.Length))
            });
        }

        return diagnostics;
    }

    private static List<FunctionDefinition> GetFunctionDefinitions(string[] lines)
    {
        var result = new List<FunctionDefinition>();

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;

            if (line.TrimEnd().EndsWith(';'))
                continue;

            var match = FunctionMultiLineRegex().Match(line);
            if (!match.Success)
                continue;

            var braceLine = -1;
            for (int i = lineIndex; i < lines.Length; i++)
            {
                if (lines[i].Contains('{'))
                {
                    braceLine = i;
                    break;
                }
            }

            if (braceLine < 0)
                continue;

            result.Add(new FunctionDefinition(
                match.Groups["name"].Value,
                lineIndex,
                match.Groups["name"].Index,
                braceLine));
        }

        return result;
    }

    private static int FindFunctionBodyEndLine(string[] lines, int braceStartLine)
    {
        int depth = 0;

        for (int i = braceStartLine; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }

            if (depth == 0)
                return i;
        }

        return -1;
    }

    private static bool ShouldWarnForMissingSemicolon(string[] lines, int lineIndex, List<Token> lineTokens)
    {
        if (lineTokens.Count == 0)
            return false;

        if (lineTokens[0].Kind == TokenKind.Directive)
        {
            return !lines[lineIndex].TrimEnd().EndsWith(';');
        }

        if (IsFunctionDefinitionLine(lines, lineIndex, lineTokens))
            return false;

        var last = lineTokens[^1].Kind;
        if (last is TokenKind.Semicolon or TokenKind.OpenBrace or TokenKind.CloseBrace or TokenKind.Colon)
            return false;

        if (IsControlFlowHeaderLine(lineTokens) || IsInsideMultiLineControlFlowHeader(lines, lineIndex))
            return false;

        if (IsStatementContinuedToNextLine(lineTokens) || IsInsideMultiLineExpression(lines, lineIndex))
            return false;

        return true;
    }

    private static bool IsStatementContinuedToNextLine(List<Token> lineTokens)
    {
        int parenDepth = 0;
        int bracketDepth = 0;

        foreach (var token in lineTokens)
        {
            switch (token.Kind)
            {
                case TokenKind.OpenParen:
                    parenDepth++;
                    break;
                case TokenKind.CloseParen:
                    parenDepth--;
                    break;
                case TokenKind.OpenBracket:
                    bracketDepth++;
                    break;
                case TokenKind.CloseBracket:
                    bracketDepth--;
                    break;
            }
        }

        return parenDepth > 0 || bracketDepth > 0;
    }

    private static bool IsInsideMultiLineExpression(string[] lines, int lineIndex)
    {
        if (lineIndex <= 0)
            return false;

        int start = lineIndex;
        while (start >= 0)
        {
            var trimmed = lines[start].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                start--;
                continue;
            }

            if (trimmed == "{" || trimmed == "}" || trimmed.EndsWith(';'))
            {
                start++;
                break;
            }

            start--;
        }

        if (start < 0)
            start = 0;

        int parenBalance = 0;
        int bracketBalance = 0;

        for (int i = start; i <= lineIndex; i++)
        {
            foreach (var c in lines[i])
            {
                if (c == '(') parenBalance++;
                else if (c == ')') parenBalance--;
                else if (c == '[') bracketBalance++;
                else if (c == ']') bracketBalance--;
            }
        }

        if (parenBalance > 0 || bracketBalance > 0)
            return true;

        var previousNonEmpty = lineIndex - 1;
        while (previousNonEmpty >= 0)
        {
            var trimmed = lines[previousNonEmpty].Trim();
            if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith("//", StringComparison.Ordinal))
            {
                previousNonEmpty--;
                continue;
            }

            return trimmed.EndsWith(',');
        }

        return false;
    }

    private static bool IsInsideMultiLineControlFlowHeader(string[] lines, int lineIndex)
    {
        if (lineIndex <= 0)
            return false;

        for (int start = lineIndex; start >= 0; start--)
        {
            var trimmed = lines[start].Trim();
            if (string.IsNullOrEmpty(trimmed))
                continue;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            if (trimmed == "{" || trimmed == "}" || trimmed.EndsWith(';'))
                return false;

            if (!StartsControlFlowHeader(trimmed))
                continue;

            var parenBalance = 0;
            for (int i = start; i <= lineIndex; i++)
            {
                foreach (var c in lines[i])
                {
                    if (c == '(') parenBalance++;
                    else if (c == ')') parenBalance--;
                }
            }

            return parenBalance >= 0;
        }

        return false;
    }

    private static bool StartsControlFlowHeader(string trimmed)
    {
        return trimmed.StartsWith("if", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("else if", StringComparison.OrdinalIgnoreCase)
            || trimmed.Equals("else", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("for", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("foreach", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("while", StringComparison.OrdinalIgnoreCase)
            || trimmed.StartsWith("switch", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsControlFlowHeaderLine(List<Token> lineTokens)
    {
        if (lineTokens.Count == 0)
            return false;

        if (lineTokens[0].Kind is not TokenKind.Keyword)
            return false;

        var keyword = lineTokens[0].Text;
        if (keyword.Equals("for", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("foreach", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("while", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("if", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("switch", StringComparison.OrdinalIgnoreCase) ||
            keyword.Equals("else", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsFunctionDefinitionLine(string[] lines, int lineIndex, List<Token> lineTokens)
    {
        for (int i = 0; i < lineTokens.Count - 1; i++)
        {
            var token = lineTokens[i];
            if (!IsNameToken(token) || lineTokens[i + 1].Kind != TokenKind.OpenParen)
                continue;

            if (IsFunctionDefinition(lines, lineIndex, token.Column, token.Text))
                return true;
        }

        return false;
    }

    private static bool IsSignificantToken(Token token)
    {
        return token.Kind is not TokenKind.Whitespace
            and not TokenKind.Comment
            and not TokenKind.EndOfFile
            and not TokenKind.BadToken;
    }

    private static bool IsNameToken(Token token)
    {
        return token.Kind is TokenKind.Identifier or TokenKind.Keyword;
    }

    private static string TryGetQualifiedPath(List<Token> lineTokens, int functionTokenIndex)
    {
        if (functionTokenIndex < 1)
            return string.Empty;

        if (lineTokens[functionTokenIndex - 1].Kind != TokenKind.DoubleColon)
            return string.Empty;

        if (functionTokenIndex >= 2 && IsNameToken(lineTokens[functionTokenIndex - 2]))
            return lineTokens[functionTokenIndex - 2].Text;

        return string.Empty;
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

        if (includedFiles.Any(f => f.Functions.Contains(functionName)))
            return true;

        // Fallback to globally indexed functions to avoid unresolved false positives
        // when symbols are known from workspace/dump but not yet in include scope.
        return FindCandidateSymbols(functionName).Any();
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
        var codeLine = StripTrailingLineComment(line);

        if (codeLine.Length == 0 || char.IsWhiteSpace(codeLine[0])) return false;
        if (matchIndex != 0) return false;
        if (codeLine.Contains(';')) return false;

        var match = FunctionMultiLineRegex().Match(codeLine);
        if (!match.Success || !match.Groups["name"].Value.Equals(functionName, StringComparison.OrdinalIgnoreCase))
            return false;

        return codeLine.Contains('{') || (lineIndex + 1 < lines.Length && StripTrailingLineComment(lines[lineIndex + 1]).TrimStart().StartsWith('{'));
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

    private static bool PathMatches(string filePath, string scriptPath)
    {
        var normalizedFile = filePath.Replace("\\", "/").ToLowerInvariant();
        var normalizedScript = scriptPath.Replace("\\", "/").ToLowerInvariant();

        if (!normalizedScript.EndsWith(".gsc"))
            normalizedScript += ".gsc";

        return normalizedFile.EndsWith(normalizedScript, StringComparison.OrdinalIgnoreCase);
    }

    private static MuteConfig ParseMuteConfig(string[] lines)
    {
        var topOfFileMutes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lineMutes = new Dictionary<int, HashSet<string>>();

        bool stillInHeader = true;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (TryParseMuteLine(trimmed, out var keys))
            {
                if (stillInHeader)
                {
                    foreach (var key in keys)
                        topOfFileMutes.Add(key);
                }

                if (i + 1 < lines.Length)
                {
                    if (!lineMutes.TryGetValue(i + 1, out var set))
                    {
                        set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        lineMutes[i + 1] = set;
                    }

                    foreach (var key in keys)
                        set.Add(key);
                }

                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            if (trimmed.StartsWith("//", StringComparison.Ordinal))
                continue;

            stillInHeader = false;
        }

        return new MuteConfig(topOfFileMutes, lineMutes);
    }

    private static bool TryParseMuteLine(string trimmedLine, out HashSet<string> keys)
    {
        keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!trimmedLine.StartsWith("//", StringComparison.Ordinal))
            return false;

        var content = trimmedLine[2..].Trim();
        if (!content.StartsWith("gsclsp-disable", StringComparison.OrdinalIgnoreCase))
            return false;

        var parts = content.Split([':', ' '], 2, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
            return false;

        var rawTargets = parts[1]
            .Split([',', ' ', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        foreach (var raw in rawTargets)
        {
            var normalized = raw.ToLowerInvariant();
            if (normalized is "all" or "warnings")
            {
                keys.Add("all");
                continue;
            }

            if (normalized is "recursive" or "recursive-function")
            {
                keys.Add(RecursiveWarningMuteKey);
                continue;
            }

            if (normalized is "semicolon" or "missing-semicolon")
            {
                keys.Add(MissingSemicolonMuteKey);
            }
        }

        return keys.Count > 0;
    }

    private static bool IsMuted(MuteConfig muteConfig, string key, int line)
    {
        if (muteConfig.TopOfFileMutes.Contains("all") || muteConfig.TopOfFileMutes.Contains(key))
            return true;

        if (muteConfig.LineMutes.TryGetValue(line, out var lineMutes) &&
            (lineMutes.Contains("all") || lineMutes.Contains(key)))
            return true;

        return false;
    }

    private static (int Start, int End) GetLineContentRange(string line)
    {
        if (string.IsNullOrEmpty(line))
            return (0, 0);

        int start = 0;
        while (start < line.Length && char.IsWhiteSpace(line[start]))
            start++;

        int end = line.Length;
        while (end > start && char.IsWhiteSpace(line[end - 1]))
            end--;

        return (start, end);
    }

    private static bool[] BuildDevBlockMask(string[] lines)
    {
        var mask = new bool[lines.Length];
        var inDevBlock = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();

            if (!inDevBlock && trimmed.StartsWith("/#", StringComparison.Ordinal))
            {
                inDevBlock = true;
                mask[i] = true;
                continue;
            }

            if (inDevBlock)
            {
                mask[i] = true;
                if (trimmed.StartsWith("#/", StringComparison.Ordinal))
                {
                    inDevBlock = false;
                }
            }
        }

        return mask;
    }

    private sealed record IncludedFileScope(string Path, HashSet<string> Functions);
    private sealed record FunctionDefinition(string Name, int DefinitionLine, int NameColumn, int BraceLine);
    private sealed record MuteConfig(HashSet<string> TopOfFileMutes, Dictionary<int, HashSet<string>> LineMutes);
}