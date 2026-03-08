using GSCLSP.Core.Indexing;
using GSCLSP.Core.Parsing;
using Microsoft.Extensions.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Text.RegularExpressions;

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

            var rawDumpPath = _configuration.GetValue<string>("gsclsp:dumpPath");
            string? normalizedDumpPath = NormalizePath(rawDumpPath);

            if (!File.Exists(currentFilePath)) return new LocationContainer();
            var currentFileLines = await File.ReadAllLinesAsync(currentFilePath, cancellationToken);
            var line = currentFileLines[request.Position.Line];
            string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character);

            if (identifier.StartsWith("::")) identifier = identifier[2..];
            if (string.IsNullOrEmpty(identifier)) return new LocationContainer();

            var locations = new List<Location>();
            var searchRegex = new Regex($@"\b{Regex.Escape(identifier)}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

            SearchFileWithRegex(currentFilePath, currentFileLines, searchRegex, locations);

            if (!string.IsNullOrEmpty(normalizedDumpPath) && Directory.Exists(normalizedDumpPath))
            {
                var gscFiles = Directory.EnumerateFiles(normalizedDumpPath, "*.?sc", SearchOption.AllDirectories);

                Parallel.ForEach(gscFiles, file =>
                {
                    if (file.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase)) return;
                    try
                    {
                        var fileLines = File.ReadAllLines(file);
                        SearchFileWithRegex(file, fileLines, searchRegex, locations);
                    }
                    catch { }
                });
            }
            else
            {
                await Console.Error.WriteLineAsync($"GSCLSP: Dump path invalid or missing: {normalizedDumpPath}");
            }

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

        private static string? NormalizePath(string path)
        {
            if (string.IsNullOrEmpty(path)) return null;

            // Handle file:///f:/ or file:///f%3A/
            string result = path.Replace("file:///", "");
            result = Uri.UnescapeDataString(result);
            result = result.Replace("/", "\\");

            return result;
        }

        private static void SearchFileWithRegex(string filePath, string[] lines, Regex regex, List<Location> locations)
        {
            for (int i = 0; i < lines.Length; i++)
            {
                var matches = regex.Matches(lines[i]);
                foreach (Match match in matches)
                {
                    AddLocationIfUnique(locations, filePath, i, match.Index, match.Length);
                }
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