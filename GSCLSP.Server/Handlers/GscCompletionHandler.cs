using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using GSCLSP.Lexer;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers
{
    public partial class GscCompletionHandler(GscIndexer indexer, GscDocumentStore documentStore) : ICompletionHandler
    {
        private readonly GscIndexer _indexer = indexer;
        private readonly GscDocumentStore _documentStore = documentStore;
        private readonly ConcurrentDictionary<string, HashSet<string>> _fileIncludesCache = [];

        public async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
        {
            var completions = new List<CompletionItem>();
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(currentFilePath);
            var currentFileLines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            if (request.Position.Line >= currentFileLines.Length) return new CompletionList();
            var line = currentFileLines[request.Position.Line];

            var lexer = new GscLexer();
            var lexed = GscLexingHelper.Lex(content);
            var token = GscLexingHelper.GetTokenAtOrBeforePosition(lexed.Tokens, request.Position.Line, request.Position.Character);

            if (token is { Kind: TokenKind.Comment or TokenKind.String })
            {
                return new CompletionList();
            }

            var appendSemicolon = !GscLexingHelper.IsInsideFunctionArgumentList(lexed.Tokens, request.Position.Line, request.Position.Character);

            int safeLength = Math.Min(request.Position.Character, line.Length);
            string lineUntilCursor = line[..safeLength];
            var trimmedLine = lineUntilCursor.TrimStart();

            if (trimmedLine.StartsWith('#') && !trimmedLine.Contains(' '))
            {
                int hashPos = lineUntilCursor.IndexOf('#');
                var directiveRange = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(request.Position.Line, hashPos),
                    request.Position
                );

                var directives = new[]
                {
                    "#include", "#using", "#inline", "#define", "#undef",
                    "#ifdef", "#ifndef", "#if", "#elif", "#elifdef", "#elifndef",
                    "#else", "#endif",
                    "#pragma", "#warning", "#error", "#line",
                    "#namespace", "#using_animtree"
                };
                foreach (var directive in directives)
                {
                    completions.Add(new CompletionItem
                    {
                        Label = directive,
                        Kind = CompletionItemKind.Keyword,
                        FilterText = directive,
                        InsertTextFormat = InsertTextFormat.PlainText,
                        TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { Range = directiveRange, NewText = directive + " " }),
                        Command = new Command { Name = "editor.action.triggerSuggest" }
                    });
                }
                return new CompletionList(completions);
            }

            // doesn't need to include anything as its just the macro
            if (trimmedLine.StartsWith("#define ") || trimmedLine.StartsWith("#else") || trimmedLine.StartsWith("#endif"))
            {
                return new CompletionList();
            }

            if (trimmedLine.StartsWith("#ifdef ") || trimmedLine.StartsWith("#ifndef ") ||
                trimmedLine.StartsWith("#elifdef ") || trimmedLine.StartsWith("#elifndef ") ||
                trimmedLine.StartsWith("#undef "))
            {
                var visibleMacros = _indexer.GetAllVisibleMacros(currentFilePath);
                foreach (var macro in visibleMacros)
                {
                    completions.Add(GscCompletionItemFactory.FromMacro(macro));
                }

                foreach (var define in GscCompletionItemFactory.BuiltInDefines)
                {
                    completions.Add(define);
                }

                return GscCompletionItemFactory.ToFilteredList(completions);
            }

            if (trimmedLine.StartsWith("#include ") || trimmedLine.StartsWith("#using ") || trimmedLine.StartsWith("#inline "))
            {
                bool isInline = trimmedLine.StartsWith("#inline ");
                var typed = trimmedLine.Split(' ', 2)[1].TrimEnd(';').Replace("/", "\\");

                var prefix = "";
                var lastSlash = typed.LastIndexOf('\\');
                if (lastSlash >= 0) prefix = typed[..(lastSlash + 1)];

                int segmentStart = lineUntilCursor.LastIndexOf('\\') + 1;
                if (segmentStart == 0) segmentStart = lineUntilCursor.LastIndexOf(' ') + 1;
                var segmentRange = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(request.Position.Line, segmentStart),
                    request.Position
                );

                var roots = new List<string>();
                if (_indexer.WorkspacePath != null) roots.Add(_indexer.WorkspacePath.Replace("\\", "/").TrimEnd('/'));
                if (_indexer.DumpPath != null) roots.Add(_indexer.DumpPath.Replace("\\", "/").TrimEnd('/'));

                var folders = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var filePath in _indexer.GetAllIndexedFilePaths())
                {
                    var normalized = filePath.Replace("\\", "/");
                    var ext = Path.GetExtension(normalized);
                    bool isGsh = ext.Equals(".gsh", StringComparison.OrdinalIgnoreCase);
                    bool isGsc = ext.Equals(".gsc", StringComparison.OrdinalIgnoreCase);

                    if (!isGsc && !isGsh) continue;
                    if (isInline && !isGsh) continue;
                    if (!isInline && isGsh) continue;

                    var relativePath = normalized;
                    foreach (var root in roots)
                    {
                        if (normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                        {
                            relativePath = normalized[(root.Length + 1)..];
                            break;
                        }
                    }

                    var gscPath = relativePath.Replace("/", "\\");
                    if (!gscPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

                    var remaining = gscPath[prefix.Length..];
                    var nextSlash = remaining.IndexOf('\\');

                    if (nextSlash >= 0)
                    {
                        var folder = remaining[..nextSlash];
                        if (folders.Add(folder))
                        {
                            completions.Add(new CompletionItem
                            {
                                Label = folder,
                                LabelDetails = new CompletionItemLabelDetails { Description = "(folder)" },
                                Kind = CompletionItemKind.Folder,
                                FilterText = folder,
                                InsertTextFormat = InsertTextFormat.PlainText,
                                TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { Range = segmentRange, NewText = folder + "\\" }),
                                Command = new Command { Name = "editor.action.triggerSuggest" }
                            });
                        }
                    }
                    else
                    {
                        var fileName = isGsh ? remaining : remaining[..^4];
                        completions.Add(new CompletionItem
                        {
                            Label = fileName,
                            LabelDetails = new CompletionItemLabelDetails { Description = isGsh ? ".gsh" : ".gsc" },
                            Kind = CompletionItemKind.File,
                            FilterText = fileName,
                            InsertTextFormat = InsertTextFormat.PlainText,
                            TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { Range = segmentRange, NewText = fileName })
                        });
                    }
                }

                return GscCompletionItemFactory.ToFilteredList(completions);
            }

            var namespaceMatch = NameSpaceRegex().Match(lineUntilCursor);
            if (namespaceMatch.Success)
            {
                string pathPrefix = namespaceMatch.Groups[1].Value.Replace("\\", "/").ToLower();

                var filteredSymbols = _indexer.WorkspaceSymbols
                    .Where(s => s.FilePath.Replace("\\", "/").Contains(pathPrefix, StringComparison.OrdinalIgnoreCase))
                    .Concat(_indexer.Symbols
                    .Where(s => s.FilePath.Replace("\\", "/").Contains(pathPrefix, StringComparison.OrdinalIgnoreCase)));

                foreach (var symbol in filteredSymbols)
                {
                    completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Method, "Global Function", appendSemicolon));
                }
                return GscCompletionItemFactory.ToFilteredList(completions);
            }

            foreach (var symbol in _indexer.WorkspaceSymbols)
            {
                bool isThisFile = symbol.FilePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase);
                completions.Add(GscCompletionItemFactory.FromSymbol(symbol,
                    isThisFile ? CompletionItemKind.Field : CompletionItemKind.Function,
                    isThisFile ? "Local Function" : "Project Function",
                    appendSemicolon));
            }

            foreach (var symbol in _indexer.Symbols)
            {
                completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Method, "Dump Function", appendSemicolon));
            }

            var includesSet = _fileIncludesCache.GetOrAdd(currentFilePath, _ => ParseIncludes(currentFileLines));

            if (includesSet.Count > 0)
            {
                foreach (var symbol in _indexer.WorkspaceSymbols.Where(s => includesSet.Contains(s.FilePath.Replace("\\", "/").ToLower())))
                {
                    completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Reference, "via #include", appendSemicolon));
                }

                foreach (var symbol in _indexer.Symbols.Where(s => includesSet.Contains(s.FilePath.Replace("\\", "/").ToLower())))
                {
                    completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Reference, "via #include", appendSemicolon));
                }
            }

            var preferBuiltInMethods = IsMethodCallCompletionContext(lineUntilCursor);

            foreach (var builtIn in _indexer.BuiltIns.GetAll(preferBuiltInMethods))
            {
                var kind = builtIn.Type == SymbolType.Method
                    ? CompletionItemKind.Method
                    : CompletionItemKind.Function;

                var source = builtIn.Type == SymbolType.Method
                    ? "Engine Built-in Method"
                    : "Engine Built-in Function";

                completions.Add(GscCompletionItemFactory.FromSymbol(builtIn, kind, source, appendSemicolon));
            }

            var macros = _indexer.GetAllVisibleMacros(currentFilePath);
            foreach (var macro in macros)
            {
                completions.Add(GscCompletionItemFactory.FromMacro(macro));
            }

            foreach (var define in GscCompletionItemFactory.BuiltInDefines)
            {
                completions.Add(define);
            }

            var funcName = GscIndexer.FindEnclosingFunctionName(currentFileLines, request.Position.Line);
            if (funcName != null)
            {
                var locals = GscIndexer.GetLocalVariables(currentFilePath, funcName, currentFileLines, request.Position.Line);
                var seenLocals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var localVar in locals)
                {
                    if (seenLocals.Add(localVar.Name))
                        completions.Add(GscCompletionItemFactory.FromLocalVariable(localVar));
                }
            }

            return GscCompletionItemFactory.ToFilteredList(completions);
        }

        private static HashSet<string> ParseIncludes(string[] fileLines)
        {
            var includesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in fileLines)
            {
                if (!GscHandlerCommon.TryExtractDirectivePath(line, out var includePath))
                    continue;

                includePath = includePath.Replace("\\", "/").ToLower();
                if (!includePath.EndsWith(".gsc")) includePath += ".gsc";
                includesSet.Add(includePath);
            }

            return includesSet;
        }

        public CompletionRegistrationOptions GetRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
        {
            return new CompletionRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("gsc"),
                TriggerCharacters = new Container<string>(":", ".", "#", "\\")
            };
        }

        private static bool IsMethodCallCompletionContext(string lineUntilCursor)
        {
            if (string.IsNullOrWhiteSpace(lineUntilCursor))
                return false;

            var i = lineUntilCursor.Length - 1;

            while (i >= 0 && char.IsWhiteSpace(lineUntilCursor[i]))
                i--;

            if (i < 0)
                return false;

            var calleeEnd = i;
            while (i >= 0 && (char.IsLetterOrDigit(lineUntilCursor[i]) || lineUntilCursor[i] == '_'))
                i--;

            if (calleeEnd == i)
                return false;

            var whitespaceCount = 0;
            while (i >= 0 && char.IsWhiteSpace(lineUntilCursor[i]))
            {
                whitespaceCount++;
                i--;
            }

            if (whitespaceCount == 0 || i < 0)
                return false;

            if (lineUntilCursor[i] is ')' or ']')
                return true;

            var callerEnd = i;
            while (i >= 0 && (char.IsLetterOrDigit(lineUntilCursor[i]) || lineUntilCursor[i] == '_'))
                i--;

            return callerEnd != i;
        }
    }
}