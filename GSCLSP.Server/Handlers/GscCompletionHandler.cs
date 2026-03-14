using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
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
#if DEBUG
            var sw = Stopwatch.StartNew();
#endif

            var completions = new List<CompletionItem>();
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(currentFilePath);
            var currentFileLines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            if (request.Position.Line >= currentFileLines.Length) return new CompletionList();
            var line = currentFileLines[request.Position.Line];

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

                var directiveMapping = new[] { ("#include", "#include "), ("#using", "#using "), ("#inline", "#inline "), ("#define", "#define ") };
                foreach (var (label, insert) in directiveMapping)
                {
                    completions.Add(new CompletionItem
                    {
                        Label = label,
                        Kind = CompletionItemKind.Keyword,
                        FilterText = label,
                        InsertTextFormat = InsertTextFormat.PlainText,
                        TextEdit = new TextEditOrInsertReplaceEdit(new TextEdit { Range = directiveRange, NewText = insert }),
                        Command = new Command { Name = "editor.action.triggerSuggest" }
                    });
                }
                return new CompletionList(completions);
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
                    completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Method, "Global Function"));
                }
                return GscCompletionItemFactory.ToFilteredList(completions);
            }

            foreach (var symbol in _indexer.WorkspaceSymbols)
            {
                bool isThisFile = symbol.FilePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase);
                completions.Add(GscCompletionItemFactory.FromSymbol(symbol,
                    isThisFile ? CompletionItemKind.Field : CompletionItemKind.Function,
                    isThisFile ? "Local Function" : "Project Function"));
            }

            foreach (var symbol in _indexer.Symbols)
            {
                completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Method, "Dump Function"));
            }

            var includesSet = _fileIncludesCache.GetOrAdd(currentFilePath, _ => ParseIncludes(currentFileLines));

            if (includesSet.Count > 0)
            {
                foreach (var symbol in _indexer.WorkspaceSymbols.Where(s => includesSet.Contains(s.FilePath.Replace("\\", "/").ToLower())))
                {
                    completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Reference, "via #include"));
                }

                foreach (var symbol in _indexer.Symbols.Where(s => includesSet.Contains(s.FilePath.Replace("\\", "/").ToLower())))
                {
                    completions.Add(GscCompletionItemFactory.FromSymbol(symbol, CompletionItemKind.Reference, "via #include"));
                }
            }

            foreach (var builtIn in _indexer.BuiltIns.GetAll())
            {
                completions.Add(GscCompletionItemFactory.FromSymbol(builtIn, CompletionItemKind.Function, "Engine Built-in"));
            }

            var macros = _indexer.GetAllVisibleMacros(currentFilePath);
            foreach (var macro in macros)
            {
                completions.Add(GscCompletionItemFactory.FromMacro(macro));
            }

            var funcName = GscIndexer.FindEnclosingFunctionName(currentFileLines, request.Position.Line);
            if (funcName != null)
            {
                var locals = GscIndexer.GetLocalVariables(currentFilePath, funcName, currentFileLines, request.Position.Line);
                foreach (var localVar in locals)
                {
                    completions.Add(GscCompletionItemFactory.FromLocalVariable(localVar));
                }
            }

#if DEBUG
            sw.Stop();
            Console.Error.WriteLine($"GSCLSP: GscCompletionHandler completed in {sw.Elapsed.TotalMilliseconds:N2}ms");
#endif

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
    }
}