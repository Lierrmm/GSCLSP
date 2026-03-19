using GSCLSP.Core.Indexing;
using GSCLSP.Core.Parsing;
using GSCLSP.Lexer;
using Microsoft.Extensions.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server.Handlers
{
    public class GscReferencesHandler(GscIndexer indexer, IConfiguration configuration) : IReferencesHandler
    {
        private readonly GscIndexer _indexer = indexer;
        private readonly IConfiguration _configuration = configuration;

        public async Task<LocationContainer> Handle(ReferenceParams request, CancellationToken cancellationToken)
        {
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            var rawDumpPath = _indexer.DumpPath ?? _configuration?.GetValue<string>("gsclsp:dumpPath");
            string? normalizedDumpPath = GscIndexer.NormalizePath(rawDumpPath);

            string currentContent = _indexer.GetFileContent(currentFilePath);
            if (string.IsNullOrEmpty(currentContent)) return new LocationContainer();

            var currentFileLines = currentContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var line = currentFileLines[request.Position.Line];
            string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character);

            if (identifier.StartsWith("::")) identifier = identifier[2..];
            if (string.IsNullOrEmpty(identifier)) return new LocationContainer();

            var locations = new List<Location>();

            // Search current file
            SearchFileWithLexer(currentFilePath, currentContent, identifier, locations);

            // Search included files
            var includedPaths = await ExtractIncludesAsync(currentFileLines, cancellationToken);
            foreach (var includePath in includedPaths)
            {
                try
                {
                    if (File.Exists(includePath))
                    {
                        var includedContent = await File.ReadAllTextAsync(includePath, cancellationToken);
                        SearchFileWithLexer(includePath, includedContent, identifier, locations);
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
                            SearchFileWithLexer(file, content, identifier, locations);
                        }
                    }
                    catch { }
                });
            }

            // Search indexed symbols
            var indexedMatches = _indexer.GetSymbolsByName(identifier);
            foreach (var symbol in indexedMatches)
            {
                if (symbol.FilePath == "Engine") continue;

                string targetPath = symbol.FilePath;
                if (!Path.IsPathRooted(targetPath) && !string.IsNullOrEmpty(normalizedDumpPath))
                    targetPath = Path.Combine(normalizedDumpPath, targetPath);

                AddLocationIfUnique(locations, targetPath, Math.Max(0, symbol.LineNumber - 1), 0, identifier.Length);
            }

            return new LocationContainer(locations);
        }

        private async Task<List<string>> ExtractIncludesAsync(string[] fileLines, CancellationToken cancellationToken)
        {
            var includePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var line in fileLines)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!GscHandlerCommon.TryExtractDirectivePath(line, out var includePath))
                    continue;

                var resolvedPath = await _indexer.GetIncludePath(includePath);
                if (!string.IsNullOrEmpty(resolvedPath))
                {
                    includePaths.Add(resolvedPath);
                }
            }

            return [.. includePaths];
        }

        private static void SearchFileWithLexer(string filePath, string content, string identifier, List<Location> locations)
        {
            var lexer = new GscLexer();
            var result = lexer.Lex(content);

            foreach (var token in result.Tokens)
            {
                if (token.Kind is not TokenKind.Identifier and not TokenKind.Keyword)
                    continue;

                if (!token.Text.Equals(identifier, StringComparison.OrdinalIgnoreCase))
                    continue;

                AddLocationIfUnique(locations, filePath, token.Line, token.Column, token.Length);
            }
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