using GSCLSP.Core.Indexing;
using GSCLSP.Core.Parsing;
using Microsoft.Extensions.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using System.Text.RegularExpressions;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers
{
    public class GscReferencesHandler(GscIndexer indexer, IConfiguration configuration) : IReferencesHandler
    {
        private readonly GscIndexer _indexer = indexer;
        private readonly IConfiguration _configuration = configuration;
        private static readonly Dictionary<string, Regex> _regexCache = [];
        private static readonly object _regexCacheLock = new();

        private static Regex GetCachedRegex(string identifier)
        {
            // Check cache first
            lock (_regexCacheLock)
            {
                if (_regexCache.TryGetValue(identifier, out var cached))
                    return cached;
            }

            // Create and cache new regex
            var pattern = $@"\b{Regex.Escape(identifier)}\b";
            var regex = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.Compiled);

            lock (_regexCacheLock)
            {
                _regexCache[identifier] = regex;
            }

            return regex;
        }

        public async Task<LocationContainer> Handle(ReferenceParams request, CancellationToken cancellationToken)
        {
            var uri = request.TextDocument.Uri;
            var currentFilePath = uri.GetFileSystemPath();

            var rawDumpPath = _configuration?.GetValue<string>("gsclsp:dumpPath");
            string? normalizedDumpPath = GscIndexer.NormalizePath(rawDumpPath);

            // Use cached content instead of File.ReadAllLinesAsync
            string currentContent = _indexer.GetFileContent(currentFilePath);
            if (string.IsNullOrEmpty(currentContent)) return new LocationContainer();
            
            var currentFileLines = currentContent.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
            var line = currentFileLines[request.Position.Line];
            string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character);

            if (identifier.StartsWith("::")) identifier = identifier[2..];
            if (string.IsNullOrEmpty(identifier)) return new LocationContainer();

            var locations = new List<Location>();
            var searchRegex = GetCachedRegex(identifier);

            // Search current file
            SearchFileWithRegex(currentFilePath, currentFileLines, searchRegex, locations);

            // Search included files
            var includedPaths = await ExtractIncludesAsync(currentFileLines, cancellationToken);
            foreach (var includePath in includedPaths)
            {
                try
                {
                    if (File.Exists(includePath))
                    {
                        var includedLines = await File.ReadAllLinesAsync(includePath, cancellationToken);
                        SearchFileWithRegex(includePath, includedLines, searchRegex, locations);
                    }
                }
                catch { }
            }

            // Search entire workspace using cached content instead of re-reading from disk
            if (!string.IsNullOrEmpty(normalizedDumpPath) && Directory.Exists(normalizedDumpPath))
            {
                var gscFiles = Directory.EnumerateFiles(normalizedDumpPath, "*.?sc", SearchOption.AllDirectories);

                Parallel.ForEach(gscFiles, file =>
                {
                    if (file.Equals(currentFilePath, StringComparison.OrdinalIgnoreCase)) return;
                    try
                    {
                        // Use cached content instead of File.ReadAllLines() - eliminates disk I/O
                        string content = _indexer.GetFileContent(file);
                        if (!string.IsNullOrEmpty(content))
                        {
                            var fileLines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
                            SearchFileWithRegex(file, fileLines, searchRegex, locations);
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
                var trimmed = line.Trim();
                
                // Try #include and #using directives
                if (trimmed.StartsWith("#include") || trimmed.StartsWith("#using"))
                {
                    var match = DirectivePathRegex().Match(line);
                    if (match.Success)
                    {
                        string includePath = match.Groups[1].Value.Trim();
                        var resolvedPath = await _indexer.GetIncludePath(includePath);
                        if (!string.IsNullOrEmpty(resolvedPath))
                        {
                            includePaths.Add(resolvedPath);
                        }
                    }
                }
                // Try #inline directives
                else if (trimmed.StartsWith("#inline"))
                {
                    var match = InlinePathRegex().Match(line);
                    if (match.Success)
                    {
                        string includePath = match.Groups[1].Value.Trim();
                        var resolvedPath = await _indexer.GetIncludePath(includePath);
                        if (!string.IsNullOrEmpty(resolvedPath))
                        {
                            includePaths.Add(resolvedPath);
                        }
                    }
                }
            }

            return [.. includePaths];
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