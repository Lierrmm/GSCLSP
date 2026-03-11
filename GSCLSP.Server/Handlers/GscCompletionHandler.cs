using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Collections.Concurrent;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers
{
    public partial class GscCompletionHandler(GscIndexer indexer) : ICompletionHandler
    {
        private readonly GscIndexer _indexer = indexer;
        private readonly ConcurrentDictionary<string, HashSet<string>> _fileIncludesCache = [];

        public async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
        {
            var completions = new List<CompletionItem>();
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            // Use cached content instead of reading from disk
            var content = _indexer.GetFileContent(currentFilePath);
            var currentFileLines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

            if (request.Position.Line >= currentFileLines.Length) return new CompletionList();
            var line = currentFileLines[request.Position.Line];

            // What did the user type before the cursor?
            int safeLength = Math.Min(request.Position.Character, line.Length);
            string lineUntilCursor = line[..safeLength];

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
                    completions.Add(CreateSymbolCompletion(symbol, CompletionItemKind.Method, "Global Function"));
                }
                return FilteredList(completions);
            }

            foreach (var symbol in _indexer.WorkspaceSymbols)
            {
                bool isThisFile = symbol.FilePath.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase);
                completions.Add(CreateSymbolCompletion(symbol,
                    isThisFile ? CompletionItemKind.Field : CompletionItemKind.Function,
                    isThisFile ? "Local Function" : "Project Function"));
            }

            var includesSet = _fileIncludesCache.GetOrAdd(currentFilePath, _ => ParseIncludes(currentFileLines));

            if (includesSet.Count > 0)
            {
                // Use HashSet for O(1) lookup
                foreach (var symbol in _indexer.WorkspaceSymbols.Where(s => includesSet.Contains(s.FilePath.Replace("\\", "/").ToLower())))
                {
                    completions.Add(CreateSymbolCompletion(symbol, CompletionItemKind.Reference, "via #include"));
                }

                foreach (var symbol in _indexer.Symbols.Where(s => includesSet.Contains(s.FilePath.Replace("\\", "/").ToLower())))
                {
                    completions.Add(CreateSymbolCompletion(symbol, CompletionItemKind.Reference, "via #include"));
                }
            }

            foreach (var builtIn in _indexer.BuiltIns.GetAll())
            {
                completions.Add(CreateSymbolCompletion(builtIn, CompletionItemKind.Function, "Engine Built-in"));
            }

            // Local variables in the enclosing function
            var funcName = GscIndexer.FindEnclosingFunctionName(currentFileLines, request.Position.Line);
            if (funcName != null)
            {
                var locals = GscIndexer.GetLocalVariables(currentFilePath, funcName, currentFileLines, request.Position.Line);
                foreach (var localVar in locals)
                {
                    completions.Add(new CompletionItem
                    {
                        Label = localVar.Name,
                        LabelDetails = new CompletionItemLabelDetails
                        {
                            Detail = $" = {localVar.Value}",
                            Description = "Local Variable"
                        },
                        Kind = CompletionItemKind.Variable,
                        InsertText = localVar.Name,
                        InsertTextFormat = InsertTextFormat.PlainText,
                        FilterText = localVar.Name
                    });
                }
            }

            return FilteredList(completions);
        }

        private static CompletionItem CreateSymbolCompletion(GscSymbol symbol, CompletionItemKind kind, string detailSource)
        {
            var argList = (symbol.Parameters ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .Where(a => !string.IsNullOrEmpty(a))
                .ToList();

            string insertText;
            if (argList.Count > 0)
            {
                var snippetParts = new List<string>();
                for (int i = 0; i < argList.Count; i++)
                {
                    // Escape special snippet characters in the param name if necessary
                    string paramName = argList[i].Replace("}", "\\}").Trim();
                    snippetParts.Add($"${{{i + 1}:{paramName}}}");
                }
                insertText = $"{symbol.Name}({string.Join(", ", snippetParts)})";
            }
            else
            {
                // No params, just add parentheses and place cursor inside
                insertText = $"{symbol.Name}($0)";
            }

            var paramString = string.IsNullOrWhiteSpace(symbol.Parameters)
                ? "()"
                : $"({symbol.Parameters.Trim()})";

            return new CompletionItem
            {
                Label = symbol.Name,
                LabelDetails = new CompletionItemLabelDetails
                {
                    Detail = $"{paramString}",
                    Description = detailSource
                },
                Kind = kind,
                Documentation = new StringOrMarkupContent(new MarkupContent
                {
                    Kind = MarkupKind.Markdown,
                    Value = $"**Source:** `{Path.GetFileName(symbol.FilePath)}`"
                }),
                InsertText = insertText,
                InsertTextFormat = InsertTextFormat.Snippet,
                FilterText = symbol.Name
            };
        }

        private static CompletionList FilteredList(IEnumerable<CompletionItem> items)
        {
            // Group by Label to prevent showing the same function twice 
            // (e.g., if it's in the index and the local scan)
            return new CompletionList(items
            .OrderByDescending(x => x.LabelDetails?.Description?.Contains("Project")) // Put local files first
            .GroupBy(x => x.Label)
            .Select(x => x.First()));
        }

        private static HashSet<string> ParseIncludes(string[] fileLines)
        {
            var includesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in fileLines)
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("#include") || trimmed.StartsWith("#using") || trimmed.StartsWith("#inline"))
                {
                    var match = IncludeRegex().Match(line);
                    if (match.Success)
                    {
                        string includePath = match.Groups[1].Value.Replace("\\", "/").ToLower();
                        if (!includePath.EndsWith(".gsc")) includePath += ".gsc";
                        includesSet.Add(includePath);
                    }
                }
            }

            return includesSet;
        }

        public CompletionRegistrationOptions GetRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
        {
            return new CompletionRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("gsc"),
                TriggerCharacters = new Container<string>(":", ".")
            };
        }
    }
}