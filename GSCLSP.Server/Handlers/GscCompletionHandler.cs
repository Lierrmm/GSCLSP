using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers
{
    public partial class GscCompletionHandler(GscIndexer indexer) : ICompletionHandler
    {
        private readonly GscIndexer _indexer = indexer;

        public async Task<CompletionList> Handle(CompletionParams request, CancellationToken cancellationToken)
        {
            var completions = new List<CompletionItem>();
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            if (!File.Exists(currentFilePath)) return new CompletionList();
            var currentFileLines = await File.ReadAllLinesAsync(currentFilePath, cancellationToken);

            if (request.Position.Line >= currentFileLines.Length) return new CompletionList();
            var line = currentFileLines[request.Position.Line];

            // What did the user type before the cursor?
            int safeLength = Math.Min(request.Position.Character, line.Length);
            string lineUntilCursor = line[..safeLength];

            var namespaceMatch = NameSpaceRegex().Match(lineUntilCursor);
            if (namespaceMatch.Success)
            {
                string pathPrefix = namespaceMatch.Groups[1].Value.Replace("\\", "/").ToLower();

                var allSymbols = _indexer.Symbols.Concat(_indexer.WorkspaceSymbols);
                var symbols = allSymbols
                    .Where(s => s.FilePath.Replace("\\", "/").Contains(pathPrefix, StringComparison.OrdinalIgnoreCase));

                foreach (var symbol in symbols)
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

            var includes = currentFileLines
                .Where(l => l.Trim().StartsWith("#include") || l.Trim().StartsWith("#using"))
                .Select(l => IncludeRegex().Match(l).Groups[2].Value.Replace("\\", "/").ToLower())
                .ToList();

            if (includes.Count != 0)
            {
                var allPotentialSymbols = _indexer.Symbols.Concat(_indexer.WorkspaceSymbols);

                var includedSymbols = allPotentialSymbols
                    .Where(s => includes.Any(inc => s.FilePath.Replace("\\", "/")
                    .Contains(inc, StringComparison.OrdinalIgnoreCase)));

                foreach (var symbol in includedSymbols)
                {
                    completions.Add(CreateSymbolCompletion(symbol, CompletionItemKind.Reference, "via #include"));
                }
            }

            foreach (var builtIn in _indexer.BuiltIns.GetAll())
            {
                completions.Add(CreateSymbolCompletion(builtIn, CompletionItemKind.Function, "Engine Built-in"));
            }

            return FilteredList(completions);
        }

        private static CompletionItem CreateSymbolCompletion(GscSymbol symbol, CompletionItemKind kind, string detailSource)
        {
            var argList = symbol.Parameters
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .ToList();

            var snippetParts = new List<string>();
            for (int i = 0; i < argList.Count; i++)
            {
                snippetParts.Add($"${{{i + 1}:{argList[i]}}}");
            }

            string snippet = $"{symbol.Name}({string.Join(", ", snippetParts)})";

            return new CompletionItem
            {
                Label = symbol.Name,
                Kind = kind,
                Detail = $"({symbol.Parameters})\n{detailSource}",
                Documentation = $"Source: {Path.GetFileName(symbol.FilePath)}",
                InsertText = snippet,
                InsertTextFormat = InsertTextFormat.Snippet,
                FilterText = symbol.Name
            };
        }

        private static CompletionList FilteredList(IEnumerable<CompletionItem> items)
        {
            // Group by Label to prevent showing the same function twice 
            // (e.g., if it's in the index and the local scan)
            return new CompletionList(items
            .OrderByDescending(x => x.Detail?.Contains("Project")) // Put local files first
            .GroupBy(x => x.Label)
            .Select(x => x.First()));
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