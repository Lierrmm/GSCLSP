using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

public partial class GscDiagnosticsHandler(GscIndexer indexer, ILanguageServerFacade languageServer)
{
    public const string UnresolvedFunctionDiagnosticCode = "gsclsp.unresolvedFunction";
    public const string RecursiveFunctionWarningCode = "gsclsp.recursiveFunction";
    public const string MissingSemicolonWarningCode = "gsclsp.missingSemicolon";
    public const string UnusedFunctionWarningCode = "gsclsp.unusedFunction";

    [Flags]
    private enum MuteSet : byte
    {
        None       = 0,
        Semicolon  = 1,
        Recursive  = 2,
        Unresolved = 4,
        Unused     = 8
    }

    private static readonly HashSet<string> ReservedWords = GscLanguageKeywords.DiagnosticReservedWords;
    private static readonly HashSet<string> KnownEntrypoints = new(StringComparer.OrdinalIgnoreCase)
    {
        "init",
        "main"
    };

    private readonly GscIndexer _indexer = indexer;
    private readonly ILanguageServerFacade _languageServer = languageServer;
    private readonly Dictionary<string, (string ContentRef, HashSet<string> Functions)> _includeFunctionsCache
        = new(StringComparer.OrdinalIgnoreCase);

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
        var muteFlags = BuildMuteFlags(lines);
        var localFunctions = GetLocalFunctions(text);
        var includedFiles = await GetIncludedFilesAsync(lines, cancellationToken);
        var localDefinitions = GetLocalFunctionDefinitions(lines);
        var calledFunctions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pointerAssignments = GetFunctionPointerAssignments(lines);
        var functionPointerCallRegex = FunctionPointerCallRegex();

        bool inBlockComment = false;

        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var line = lines[lineIndex];
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#'))
            {
                if (GscHandlerCommon.IsIncludeLikeDirective(trimmed) &&
                    !trimmed.TrimEnd().EndsWith(';') &&
                    !IsMuted(muteFlags, lineIndex, MuteSet.Semicolon))
                {
                    diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Warning,
                        Source = "gsclsp",
                        Code = MissingSemicolonWarningCode,
                        Message = "Directive should end with ';'.",
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(lineIndex, 0),
                            new Position(lineIndex, line.Length))
                    });
                }

                GscHandlerCommon.GetCodeRanges(line, ref inBlockComment);
                continue;
            }

            var codeRanges = GscHandlerCommon.GetCodeRanges(line, ref inBlockComment);
            if (codeRanges.Count == 0) continue;

            var enclosingFunction = GscIndexer.FindEnclosingFunctionName(lines, lineIndex);

            foreach (var (start, end) in codeRanges)
            {
                var segment = line[start..end];
                if (!segment.Contains('('))
                    continue;

                foreach (var callSite in new CallSiteScanner(segment))
                {
                    var functionName = callSite.Name;
                    if (ReservedWords.Contains(functionName))
                        continue;

                    if (IsFunctionDefinition(lines, lineIndex, start + callSite.MatchIndex, functionName))
                        continue;

                    calledFunctions.Add(functionName);

                    if (!string.IsNullOrEmpty(enclosingFunction) &&
                        functionName.Equals(enclosingFunction, StringComparison.OrdinalIgnoreCase) &&
                        !IsMuted(muteFlags, lineIndex, MuteSet.Recursive))
                    {
                        diagnostics.Add(new Diagnostic
                        {
                            Severity = DiagnosticSeverity.Warning,
                            Source = "gsclsp",
                            Code = RecursiveFunctionWarningCode,
                            Message = $"Function '{functionName}' calls itself (recursive call).",
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(lineIndex, start + callSite.NameIndex),
                                new Position(lineIndex, start + callSite.NameIndex + functionName.Length))
                        });
                    }

                    if (IsVisibleFunction(filePath, functionName, callSite.Path, localFunctions, includedFiles))
                        continue;

                    if (IsMuted(muteFlags, lineIndex, MuteSet.Unresolved))
                        continue;

                    diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Error,
                        Source = "gsclsp",
                        Code = UnresolvedFunctionDiagnosticCode,
                        Data = functionName,
                        Message = $"Function '{functionName}' is not defined in this file or its included files.",
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(lineIndex, start + callSite.NameIndex),
                            new Position(lineIndex, start + callSite.NameIndex + functionName.Length))
                    });
                }

                if (!segment.Contains("[[", StringComparison.Ordinal))
                    continue;

                for (var pointerCall = functionPointerCallRegex.Match(segment); pointerCall.Success; pointerCall = pointerCall.NextMatch())
                {
                    var targetGroup = pointerCall.Groups["target"];
                    var targetName = targetGroup.Value.Trim();
                    if (string.IsNullOrEmpty(targetName))
                        continue;

                    if (!pointerAssignments.TryGetValue(targetName, out var resolvedTarget))
                        continue;

                    calledFunctions.Add(resolvedTarget.FunctionName);

                    if (!string.IsNullOrEmpty(enclosingFunction) &&
                        resolvedTarget.FunctionName.Equals(enclosingFunction, StringComparison.OrdinalIgnoreCase) &&
                        !IsMuted(muteFlags, lineIndex, MuteSet.Recursive))
                    {
                        diagnostics.Add(new Diagnostic
                        {
                            Severity = DiagnosticSeverity.Warning,
                            Source = "gsclsp",
                            Code = RecursiveFunctionWarningCode,
                            Message = $"Function '{resolvedTarget.FunctionName}' calls itself (recursive call).",
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(lineIndex, start + targetGroup.Index),
                                new Position(lineIndex, start + targetGroup.Index + targetGroup.Length))
                        });
                    }

                    if (IsVisibleFunction(filePath, resolvedTarget.FunctionName, resolvedTarget.QualifiedPath, localFunctions, includedFiles))
                        continue;

                    if (IsMuted(muteFlags, lineIndex, MuteSet.Unresolved))
                        continue;

                    diagnostics.Add(new Diagnostic
                    {
                        Severity = DiagnosticSeverity.Error,
                        Source = "gsclsp",
                        Code = UnresolvedFunctionDiagnosticCode,
                        Data = resolvedTarget.FunctionName,
                        Message = $"Function '{resolvedTarget.FunctionName}' is not defined in this file or its included files.",
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                            new Position(lineIndex, start + targetGroup.Index),
                            new Position(lineIndex, start + targetGroup.Index + targetGroup.Length))
                    });
                }
            }

            if (TryFindStatementWithoutSemicolon(lines, lineIndex, line, codeRanges, out var warnStart, out var warnEnd) &&
                !IsMuted(muteFlags, lineIndex, MuteSet.Semicolon))
            {
                diagnostics.Add(new Diagnostic
                {
                    Severity = DiagnosticSeverity.Warning,
                    Source = "gsclsp",
                    Code = MissingSemicolonWarningCode,
                    Message = "Statement should end with ';'.",
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(lineIndex, warnStart),
                        new Position(lineIndex, warnEnd))
                });
            }
        }

        foreach (var def in localDefinitions)
        {
            if (KnownEntrypoints.Contains(def.Name))
                continue;

            if (calledFunctions.Contains(def.Name))
                continue;

            if (IsMuted(muteFlags, def.LineIndex, MuteSet.Unused))
                continue;

            diagnostics.Add(new Diagnostic
            {
                Severity = DiagnosticSeverity.Warning,
                Source = "gsclsp",
                Code = UnusedFunctionWarningCode,
                Message = $"Function '{def.Name}' appears unused in this file.",
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(def.LineIndex, def.NameIndex),
                    new Position(def.LineIndex, def.NameIndex + def.Name.Length))
            });
        }

        return diagnostics;
    }

    private static Dictionary<string, FunctionPointerTarget> GetFunctionPointerAssignments(string[] lines)
    {
        var result = new Dictionary<string, FunctionPointerTarget>(StringComparer.OrdinalIgnoreCase);
        var functionPointerAssignmentRegex = FunctionPointerAssignmentRegex();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (!line.Contains('=') || !line.Contains("::", StringComparison.Ordinal) || !line.Contains(';'))
                continue;

            var match = functionPointerAssignmentRegex.Match(line);
            if (!match.Success)
                continue;

            var lhs = match.Groups["lhs"].Value.Trim();
            var name = match.Groups["name"].Value.Trim();
            var path = match.Groups["path"].Value.Trim();

            if (string.IsNullOrWhiteSpace(lhs) || string.IsNullOrWhiteSpace(name))
                continue;

            result[lhs] = new FunctionPointerTarget(name, path);
        }

        return result;
    }

    private static MuteSet[] BuildMuteFlags(string[] lines)
    {
        var flags = new MuteSet[lines.Length];
        for (int i = 0; i < lines.Length; i++)
            flags[i] = ComputeLineMuteFlags(lines[i]);
        return flags;
    }

    private static MuteSet ComputeLineMuteFlags(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return MuteSet.None;

        var lower = line.ToLowerInvariant();
        if (!lower.Contains("gsclsp-ignore"))
            return MuteSet.None;

        if (lower.Contains("gsclsp-ignore all"))
            return MuteSet.Semicolon | MuteSet.Recursive | MuteSet.Unresolved | MuteSet.Unused;

        var result = MuteSet.None;
        if (MuteSemicolonRegex().IsMatch(lower))  result |= MuteSet.Semicolon;
        if (MuteRecursiveRegex().IsMatch(lower))  result |= MuteSet.Recursive;
        if (MuteUnresolvedRegex().IsMatch(lower)) result |= MuteSet.Unresolved;
        if (MuteUnusedRegex().IsMatch(lower))     result |= MuteSet.Unused;
        return result;
    }

    private static bool IsMuted(MuteSet[] muteFlags, int lineIndex, MuteSet flag)
    {
        if ((uint)lineIndex >= (uint)muteFlags.Length)
            return false;

        return (muteFlags[lineIndex] & flag) != 0 ||
               (lineIndex > 0 && (muteFlags[lineIndex - 1] & flag) != 0);
    }

    private static List<LocalFunctionDefinition> GetLocalFunctionDefinitions(string[] lines)
    {
        var result = new List<LocalFunctionDefinition>();

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;

            if (line.Contains(';'))
                continue;

            var match = FunctionMultiLineRegex().Match(line);
            if (!match.Success)
                continue;

            var nameGroup = match.Groups["name"];
            if (string.IsNullOrEmpty(nameGroup.Value))
                continue;

            bool hasBrace = line.Contains('{') || (i + 1 < lines.Length && lines[i + 1].TrimStart().StartsWith('{'));
            if (!hasBrace)
                continue;

            result.Add(new LocalFunctionDefinition(nameGroup.Value, i, nameGroup.Index));
        }

        return result;
    }

    private static HashSet<string> GetLocalFunctions(string text)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var matches = FunctionMultiLineRegex().Matches(text);

        foreach (System.Text.RegularExpressions.Match match in matches)
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

            if (!GscHandlerCommon.TryExtractDirectivePath(line, out var includePath, includeInline: false))
                continue;

            var resolvedPath = await _indexer.GetIncludePath(includePath);
            if (string.IsNullOrEmpty(resolvedPath) || !seen.Add(resolvedPath))
                continue;

            var readablePath = ResolveReadablePath(resolvedPath);
            var content = _indexer.GetFileContent(readablePath);

            HashSet<string> functions;
            if (_includeFunctionsCache.TryGetValue(readablePath, out var cached) &&
                ReferenceEquals(cached.ContentRef, content))
            {
                functions = cached.Functions;
            }
            else
            {
                functions = GetLocalFunctions(content);
                _includeFunctionsCache[readablePath] = (content, functions);
            }

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
        var normalizedFile = GscIndexer.NormalizePath(filePath);
        var normalizedScript = GscIndexer.NormalizePath(scriptPath);

        if (!normalizedScript.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase))
            normalizedScript += ".gsc";

        return normalizedFile.EndsWith(normalizedScript, StringComparison.OrdinalIgnoreCase);
    }

    private sealed record IncludedFileScope(string Path, HashSet<string> Functions);
    private sealed record LocalFunctionDefinition(string Name, int LineIndex, int NameIndex);
    private sealed record FunctionPointerTarget(string FunctionName, string QualifiedPath);

    private static bool TryFindStatementWithoutSemicolon(string[] lines, int lineIndex, string line, List<(int Start, int End)> codeRanges, out int warnStart, out int warnEnd)
    {
        warnStart = 0;
        warnEnd = 0;

        if (codeRanges.Count == 0)
            return false;

        string combined = string.Concat(codeRanges.Select(r => line[r.Start..r.End]));
        var segment = combined.Trim();
        if (string.IsNullOrEmpty(segment))
            return false;

        if (segment.EndsWith(';') || segment.EndsWith('{') || segment.EndsWith('}'))
            return false;

        if (segment.Equals("else", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("if ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("if(", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("while ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("while(", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("for ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("for(", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("switch", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("case ", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("default", StringComparison.OrdinalIgnoreCase) ||
            segment.StartsWith("do", StringComparison.OrdinalIgnoreCase))
            return false;

        var firstScan = new CallSiteScanner(segment);
        if (firstScan.MoveNext() &&
            IsFunctionDefinition(lines, lineIndex, 0, firstScan.Current.Name))
            return false;

        warnStart = codeRanges[0].Start;
        while (warnStart < line.Length && char.IsWhiteSpace(line[warnStart]))
            warnStart++;

        warnEnd = codeRanges[^1].End;
        while (warnEnd > warnStart && char.IsWhiteSpace(line[warnEnd - 1]))
            warnEnd--;

        return warnEnd > warnStart;
    }

    // Span-based call-site scanner: replaces CallSiteRegex in the hot loop.
    private readonly record struct CallSite(int MatchIndex, int NameIndex, string Name, string Path);

    private struct CallSiteScanner
    {
        private readonly string _s;
        private int _pos;

        public CallSite Current { get; private set; }

        public CallSiteScanner(string segment)
        {
            _s = segment;
            _pos = 0;
        }

        // Duck-typed foreach: returns self so the compiler uses this struct directly.
        public readonly CallSiteScanner GetEnumerator() => this;

        public bool MoveNext()
        {
            int len = _s.Length;

            while (_pos < len)
            {
                char c = _s[_pos];

                // Global :: prefix
                if (c == ':' && _pos + 1 < len && _s[_pos + 1] == ':')
                {
                    int matchStart = _pos;
                    int nameStart  = _pos + 2;
                    if (nameStart < len && IsIdentStart(_s[nameStart]))
                    {
                        int nameEnd = ScanIdent(nameStart + 1, len);
                        int j = SkipWs(nameEnd, len);
                        if (j < len && _s[j] == '(')
                        {
                            Current = new CallSite(matchStart, nameStart, _s[nameStart..nameEnd], string.Empty);
                            _pos = nameEnd;
                            return true;
                        }
                    }
                    _pos += 2;
                    continue;
                }

                if (!IsIdentStart(c)) { _pos++; continue; }

                // Identifier
                int identStart = _pos;
                int identEnd   = ScanIdent(_pos + 1, len);

                // Path with backslash segments: word\word\...\word::name
                if (identEnd < len && _s[identEnd] == '\\')
                {
                    int pathEnd = identEnd;
                    while (pathEnd < len && _s[pathEnd] == '\\')
                    {
                        int segStart = pathEnd + 1;
                        if (segStart >= len || !IsIdentStart(_s[segStart])) break;
                        pathEnd = ScanIdent(segStart + 1, len);
                    }

                    if (pathEnd + 1 < len && _s[pathEnd] == ':' && _s[pathEnd + 1] == ':')
                    {
                        int nameStart = pathEnd + 2;
                        if (nameStart < len && IsIdentStart(_s[nameStart]))
                        {
                            int nameEnd = ScanIdent(nameStart + 1, len);
                            int j = SkipWs(nameEnd, len);
                            if (j < len && _s[j] == '(')
                            {
                                Current = new CallSite(identStart, nameStart, _s[nameStart..nameEnd], _s[identStart..pathEnd]);
                                _pos = nameEnd;
                                return true;
                            }
                        }
                    }
                    _pos = identEnd;
                    continue;
                }

                // Simple qualifier: word::name
                if (identEnd + 1 < len && _s[identEnd] == ':' && _s[identEnd + 1] == ':')
                {
                    int nameStart = identEnd + 2;
                    if (nameStart < len && IsIdentStart(_s[nameStart]))
                    {
                        int nameEnd = ScanIdent(nameStart + 1, len);
                        int j = SkipWs(nameEnd, len);
                        if (j < len && _s[j] == '(')
                        {
                            Current = new CallSite(identStart, nameStart, _s[nameStart..nameEnd], _s[identStart..identEnd]);
                            _pos = nameEnd;
                            return true;
                        }
                    }
                    _pos = identEnd;
                    continue;
                }

                // Direct call: name(
                {
                    int j = SkipWs(identEnd, len);
                    if (j < len && _s[j] == '(')
                    {
                        Current = new CallSite(identStart, identStart, _s[identStart..identEnd], string.Empty);
                        _pos = identEnd;
                        return true;
                    }
                }

                _pos = identEnd;
            }

            return false;
        }

        private readonly int ScanIdent(int start, int len)
        {
            while (start < len && IsIdentCont(_s[start])) start++;
            return start;
        }

        private readonly int SkipWs(int start, int len)
        {
            while (start < len && (_s[start] == ' ' || _s[start] == '\t')) start++;
            return start;
        }

        private static bool IsIdentStart(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_';

        private static bool IsIdentCont(char c) =>
            (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || c == '_' || (c >= '0' && c <= '9');
    }
}
