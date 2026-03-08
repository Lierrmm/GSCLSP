using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Text.RegularExpressions;

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

            var namespaceMatch = namespaceRegex().Match(lineUntilCursor);
            if (namespaceMatch.Success)
            {
                string pathPrefix = namespaceMatch.Groups[1].Value.Replace("\\", "/").ToLower();
                var symbols = _indexer.Symbols
                    .Where(s => s.FilePath.Replace("\\", "/").Contains(pathPrefix, StringComparison.CurrentCultureIgnoreCase));

                foreach (var symbol in symbols)
                {
                    completions.Add(CreateSymbolCompletion(symbol, CompletionItemKind.Method, "Global Function"));
                }
                return FilteredList(completions);
            }

            // We parse the current buffer to find functions defined in the same file
            var localFunctions = ParseLocalFunctions(currentFileLines);
            foreach (var local in localFunctions)
            {
                completions.Add(CreateSymbolCompletion(local, CompletionItemKind.Field, "Local Function"));
            }

            var includes = currentFileLines
                .Where(l => l.Trim().StartsWith("#include"))
                .Select(l => includeRegex().Match(l).Groups[1].Value.Replace("\\", "/").ToLower())
                .ToList();

            if (includes.Count != 0)
            {
                var includedSymbols = _indexer.Symbols
                    .Where(s => includes.Any(inc => s.FilePath.Replace("\\", "/").Contains(inc, StringComparison.CurrentCultureIgnoreCase)));

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

        private static List<GscSymbol> ParseLocalFunctions(string[] lines)
        {
            var locals = new List<GscSymbol>();
            // function_name( arg1, arg2 )
            var funcRegex = functionDefinitionRegex();
            var content = string.Join("\n", lines);

            foreach (Match m in funcRegex.Matches(content))
            {
                locals.Add(new GscSymbol(
                    m.Groups[1].Value,
                    "current_file",
                    0,
                    m.Groups[2].Value,
                    SymbolType.Function
                ));
            }
            return locals;
        }

        private static CompletionItem CreateSymbolCompletion(GscSymbol symbol, CompletionItemKind kind, string detailSource)
        {
            var argList = symbol.Parameters
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(a => a.Trim())
                .ToList();

            // Build Snippet: func(${1:arg1}, ${2:arg2})
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
                Documentation = symbol.FilePath != "current_file" ? $"Path: {symbol.FilePath}" : "Defined in this file",
                InsertText = snippet,
                InsertTextFormat = InsertTextFormat.Snippet,
                FilterText = symbol.Name
            };
        }

        private static CompletionList FilteredList(IEnumerable<CompletionItem> items)
        {
            // Group by Label to prevent showing the same function twice 
            // (e.g., if it's in the index and the local scan)
            return new CompletionList(items.GroupBy(x => x.Label).Select(x => x.First()));
        }

        public CompletionRegistrationOptions GetRegistrationOptions(CompletionCapability capability, ClientCapabilities clientCapabilities)
        {
            return new CompletionRegistrationOptions
            {
                DocumentSelector = TextDocumentSelector.ForLanguage("gsc"),
                TriggerCharacters = new Container<string>(":")
            };
        }

        [GeneratedRegex(@"([\w\\]+)::$")]
        private static partial Regex namespaceRegex();
        [GeneratedRegex(@"#include\s+([\w\\]+)")]
        private static partial Regex includeRegex();
        [GeneratedRegex(@"^(\w+)\s*\((.*?)\)", RegexOptions.Multiline)]
        private static partial Regex functionDefinitionRegex();
    }
}