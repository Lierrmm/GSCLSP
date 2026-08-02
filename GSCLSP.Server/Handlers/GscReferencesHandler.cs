using System.Collections.Concurrent;
using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using GSCLSP.Core.Parsing;
using GSCLSP.Lexer;
using Microsoft.Extensions.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server.Handlers
{
    public class GscReferencesHandler(GscIndexer indexer, GscDocumentStore documentStore, IConfiguration configuration) : IReferencesHandler
    {
        private readonly GscIndexer _indexer = indexer;
        private readonly GscDocumentStore _documentStore = documentStore;
        private readonly IConfiguration _configuration = configuration;

        private enum ReferenceKind { Function, LocalVariable, MemberField, FileScoped, Macro }

        private sealed record ReferenceTarget(
            ReferenceKind Kind,
            string Name,
            string? DefiningPath,
            (int StartLine, int EndLine)? FunctionBodyRange,
            bool WorkspaceWide);

        public async Task<LocationContainer?> Handle(ReferenceParams request, CancellationToken cancellationToken)
        {
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            var rawDumpPath = _indexer.DumpPath ?? _configuration?.GetValue<string>("gsclsp:dumpPath");
            string? normalizedDumpPath = GscIndexer.NormalizePath(rawDumpPath);

            string currentContent = _documentStore.Get(uri) ?? _indexer.GetFileContent(currentFilePath);
            if (string.IsNullOrEmpty(currentContent) || GscCompiledScriptDetector.IsCompiledText(currentContent)) return new LocationContainer();

            var currentFileLines = currentContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            if (request.Position.Line >= currentFileLines.Length) return new LocationContainer();

            var lexed = GscLexingHelper.Lex(currentContent);
            int cursorIndex = FindIdentifierTokenIndex(lexed.Tokens, request.Position);
            if (cursorIndex < 0) return new LocationContainer();

            var target = ResolveTarget(lexed.Tokens, cursorIndex, currentFileLines, currentFilePath, normalizedDumpPath);
            if (target is null) return new LocationContainer();

            var locations = new List<Location>();

            // Scoped kinds never leave the current document.
            if (target.Kind is ReferenceKind.LocalVariable or ReferenceKind.MemberField or ReferenceKind.FileScoped)
            {
                CollectMatches(currentFilePath, lexed.Tokens, currentFileLines, target, locations, normalizedDumpPath);
                return new LocationContainer(locations);
            }

            CollectMatches(currentFilePath, lexed.Tokens, currentFileLines, target, locations, normalizedDumpPath);

            if (target.Kind == ReferenceKind.Macro && !target.WorkspaceWide)
                return new LocationContainer(locations);

            // Search included files
            var includedPaths = await ExtractIncludesAsync(currentFileLines, cancellationToken);
            foreach (var includePath in includedPaths)
            {
                try
                {
                    if (File.Exists(includePath))
                    {
                        var includedContent = await File.ReadAllTextAsync(includePath, cancellationToken);
                        SearchFile(includePath, includedContent, target, locations, normalizedDumpPath);
                    }
                }
                catch { }
            }

            // Search entire workspace using cached content
            if (!string.IsNullOrEmpty(normalizedDumpPath) && Directory.Exists(normalizedDumpPath))
            {
                var gscFiles = Directory.EnumerateFiles(normalizedDumpPath, "*.?sc", SearchOption.AllDirectories);

                Parallel.ForEach(gscFiles, file =>
                {
                    if (file.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase)) return;
                    try
                    {
                        string content = _indexer.GetFileContent(file);
                        if (!string.IsNullOrEmpty(content))
                        {
                            SearchFile(file, content, target, locations, normalizedDumpPath);
                        }
                    }
                    catch { }
                });
            }

            // Search indexed symbols, but only the ones that are the resolved definition
            if (target.Kind == ReferenceKind.Function)
            {
                foreach (var symbol in _indexer.GetSymbolsByName(target.Name))
                {
                    if (symbol.FilePath == "Engine") continue;

                    var symbolPath = ToFullPath(symbol.FilePath, normalizedDumpPath);
                    if (symbolPath is null) continue;

                    if (target.DefiningPath is not null &&
                        !symbolPath.Equals(target.DefiningPath, StringComparison.OrdinalIgnoreCase))
                        continue;

                    AddLocationIfUnique(locations, symbolPath, Math.Max(0, symbol.LineNumber - 1), 0, target.Name.Length);
                }
            }

            return new LocationContainer(locations);
        }

        private ReferenceTarget? ResolveTarget(IReadOnlyList<Token> tokens, int cursorIndex, string[] lines, string currentFilePath, string? dumpPath)
        {
            var token = tokens[cursorIndex];
            var name = token.Text;
            if (string.IsNullOrEmpty(name)) return null;

            // Cursor sits on the namespace part of 'ns::func' - nothing meaningful to list.
            if (GscVariableTokenFilter.FindSignificantToken(tokens, cursorIndex, 1)?.Kind is TokenKind.DoubleColon)
                return null;

            var line = lines[token.Line];

            var fileMacros = GscIndexer.GetFileMacros(currentFilePath);
            if (fileMacros.Any(m => m.Name.Equals(name, StringComparison.Ordinal)))
                return new ReferenceTarget(ReferenceKind.Macro, name, null, null, false);

            if (_indexer.ResolveMacro(currentFilePath, name) != null)
                return new ReferenceTarget(ReferenceKind.Macro, name, null, null, true);

            if (IsFunctionUsage(tokens, cursorIndex))
            {
                var qualified = QualifiedNameAt(line, token.Column, name);
                var symbol = _indexer.ResolveFunction(currentFilePath, qualified).Symbol;
                var definingPath = ToFullPath(symbol?.FilePath, dumpPath);
                return new ReferenceTarget(ReferenceKind.Function, name, definingPath, null, true);
            }

            if (GscVariableTokenFilter.FindSignificantToken(tokens, cursorIndex, -1)?.Kind is TokenKind.Dot)
                return new ReferenceTarget(ReferenceKind.MemberField, name, null, null, false);

            var bodyRange = FindEnclosingFunctionBodyRange(lines, token.Line);
            if (bodyRange is not null)
                return new ReferenceTarget(ReferenceKind.LocalVariable, name, null, bodyRange, false);

            return new ReferenceTarget(ReferenceKind.FileScoped, name, null, null, false);
        }

        private async Task<List<string>> ExtractIncludesAsync(string[] fileLines, CancellationToken cancellationToken)
        {
            var includePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in fileLines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!GscHandlerCommon.TryExtractDirectivePath(line, out var includePath))
                    continue;

                var resolvedPath = await _indexer.GetIncludePathAsync(includePath);
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    includePaths.Add(resolvedPath);
                }
            }

            return [.. includePaths];
        }

        private void SearchFile(string filePath, string content, ReferenceTarget target, List<Location> locations, string? dumpPath)
        {
            if (!content.Contains(target.Name, StringComparison.OrdinalIgnoreCase)) return;

            var lexed = GscLexingHelper.Lex(content);
            var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            CollectMatches(filePath, lexed.Tokens, lines, target, locations, dumpPath);
        }

        private void CollectMatches(string filePath, IReadOnlyList<Token> tokens, string[] lines, ReferenceTarget target, List<Location> locations, string? dumpPath)
        {
            var resolutionCache = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];

                if (token.Kind is TokenKind.Directive)
                {
                    if (target.Kind == ReferenceKind.Macro && MacroDirectiveMatches(token, target.Name, out int defineCol))
                        AddLocationIfUnique(locations, filePath, token.Line, defineCol, target.Name.Length);
                    continue;
                }

                if (token.Kind is not TokenKind.Identifier and not TokenKind.Keyword) continue;
                if (!token.Text.Equals(target.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (token.Line >= lines.Length) continue;

                bool isFunctionUsage = IsFunctionUsage(tokens, i);

                switch (target.Kind)
                {
                    case ReferenceKind.Function:
                        if (!isFunctionUsage) continue;
                        if (!FunctionUsageResolvesToTarget(filePath, lines[token.Line], token, target, dumpPath, resolutionCache)) continue;
                        break;

                    case ReferenceKind.LocalVariable:
                        var (start, end) = target.FunctionBodyRange!.Value;
                        if (token.Line < start || token.Line > end) continue;
                        if (isFunctionUsage) continue;
                        if (GscVariableTokenFilter.IsNamespaceOrMemberUsage(tokens, i)) continue;
                        break;

                    case ReferenceKind.MemberField:
                        if (GscVariableTokenFilter.FindSignificantToken(tokens, i, -1)?.Kind is not TokenKind.Dot) continue;
                        break;

                    case ReferenceKind.FileScoped:
                        if (isFunctionUsage) continue;
                        if (GscVariableTokenFilter.IsNamespaceOrMemberUsage(tokens, i)) continue;
                        break;

                    case ReferenceKind.Macro:
                        break;
                }

                AddLocationIfUnique(locations, filePath, token.Line, token.Column, token.Length);
            }
        }

        private bool FunctionUsageResolvesToTarget(string filePath, string line, Token token, ReferenceTarget target, string? dumpPath, Dictionary<string, bool> cache)
        {
            // No known definition (engine builtin or unindexed) - call shape is all we can verify.
            if (target.DefiningPath is null) return true;

            var qualified = QualifiedNameAt(line, token.Column, target.Name);

            lock (cache)
            {
                if (cache.TryGetValue(qualified, out var cached)) return cached;
            }

            var symbol = _indexer.ResolveFunction(filePath, qualified).Symbol;
            var resolvedPath = ToFullPath(symbol?.FilePath, dumpPath);
            bool matches = resolvedPath is not null && resolvedPath.Equals(target.DefiningPath, StringComparison.OrdinalIgnoreCase);

            lock (cache)
            {
                cache[qualified] = matches;
            }

            return matches;
        }

        // Returns 'ns::name' / 'maps\mp\_util::name' when the occurrence is qualified, otherwise the bare name.
        private static string QualifiedNameAt(string line, int column, string fallback)
        {
            var full = GscWordScanner.GetFullIdentifierAt(line, column);
            if (string.IsNullOrEmpty(full)) return fallback;
            if (full.StartsWith("::", StringComparison.Ordinal)) full = full[2..];
            return string.IsNullOrEmpty(full) ? fallback : full;
        }

        private static bool IsFunctionUsage(IReadOnlyList<Token> tokens, int index)
        {
            if (GscVariableTokenFilter.FindSignificantToken(tokens, index, 1)?.Kind is TokenKind.OpenParen)
                return true;

            // '&func' pointers and 'ns::func' references are function usages even without a call.
            return GscVariableTokenFilter.FindSignificantToken(tokens, index, -1)?.Kind is TokenKind.Ampersand or TokenKind.DoubleColon;
        }

        private static bool MacroDirectiveMatches(Token token, string name, out int column)
        {
            column = 0;
            var text = token.Text;
            if (string.IsNullOrEmpty(text)) return false;

            var trimStart = text.TrimStart();
            int leadingWs = text.Length - trimStart.Length;
            if (!trimStart.StartsWith("#define", StringComparison.Ordinal)) return false;

            var afterDefine = trimStart[7..];
            int nameStart = leadingWs + 7 + (afterDefine.Length - afterDefine.TrimStart().Length);
            int nameEnd = nameStart;
            while (nameEnd < text.Length && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] == '_'))
                nameEnd++;

            if (nameEnd - nameStart != name.Length) return false;
            if (!text.AsSpan(nameStart, name.Length).Equals(name.AsSpan(), StringComparison.Ordinal)) return false;

            column = token.Column + nameStart;
            return true;
        }

        private static int FindIdentifierTokenIndex(IReadOnlyList<Token> tokens, Position position)
        {
            int best = -1;
            for (int i = 0; i < tokens.Count; i++)
            {
                var token = tokens[i];
                if (token.Length == 0 || token.Line != position.Line) continue;
                if (token.Kind is not TokenKind.Identifier and not TokenKind.Keyword) continue;

                if (position.Character >= token.Column && position.Character < token.Column + token.Length)
                    return i;

                // cursor parked just past the identifier
                if (position.Character == token.Column + token.Length) best = i;
            }

            return best;
        }

        private static (int StartLine, int EndLine)? FindEnclosingFunctionBodyRange(string[] lines, int cursorLine)
        {
            int funcDefLine = -1;
            for (int i = cursorLine; i >= 0; i--)
            {
                var ln = lines[i];
                if (ln.Length == 0) continue;
                if (char.IsWhiteSpace(ln[0])) continue;
                if (ln.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (ln.Contains(';')) continue;

                var match = RegexPatterns.FunctionMultiLineRegex().Match(ln);
                if (match.Success && match.Index == 0)
                {
                    funcDefLine = i;
                    break;
                }
            }
            if (funcDefLine < 0) return null;

            int braceStart = -1;
            for (int i = funcDefLine; i < lines.Length; i++)
            {
                if (lines[i].Contains('{')) { braceStart = i; break; }
            }
            if (braceStart < 0) return null;

            int depth = 0;
            int braceEnd = lines.Length - 1;
            for (int i = braceStart; i < lines.Length; i++)
            {
                foreach (char c in lines[i])
                {
                    if (c == '{') depth++;
                    else if (c == '}') depth--;
                }
                if (depth == 0) { braceEnd = i; break; }
            }

            if (cursorLine < funcDefLine || cursorLine > braceEnd) return null;
            return (funcDefLine, braceEnd);
        }

        private static string? ToFullPath(string? path, string? dumpPath)
        {
            if (string.IsNullOrEmpty(path) || path == "Engine") return null;

            var normalized = path.Replace('/', '\\');
            if (!Path.IsPathRooted(normalized) && !string.IsNullOrEmpty(dumpPath))
                normalized = Path.Combine(dumpPath, normalized);

            try { return Path.GetFullPath(normalized); }
            catch { return normalized; }
        }

        private static void AddLocationIfUnique(List<Location> locations, string path, int line, int col, int length)
        {
            lock (locations)
            {
                bool exists = locations.Any(l =>
                    l.Uri.GetFileSystemPath().Equals(path, StringComparison.OrdinalIgnoreCase) &&
                    l.Range.Start.Line == line &&
                    l.Range.Start.Character == col);

                if (!exists)
                {
                    locations.Add(new Location
                    {
                        Uri = DocumentUri.FromFileSystemPath(path),
                        Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(new Position(line, col), new Position(line, col + length))
                    });
                }
            }
        }

        public ReferenceRegistrationOptions GetRegistrationOptions(ReferenceCapability capability, ClientCapabilities clientCapabilities)
        {
            return new ReferenceRegistrationOptions { DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "csc") };
        }
    }
}
