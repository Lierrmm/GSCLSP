using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using GSCLSP.Core.Parsing;
using Microsoft.Extensions.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server;
using DefinitionResult = OmniSharp.Extensions.LanguageServer.Protocol.Models.LocationOrLocationLinks;

namespace GSCLSP.Server.Handlers;

public class GscDefinitionHandler(GscIndexer indexer, ILanguageServerConfiguration configuration) : IDefinitionHandler
{
    private readonly GscIndexer _indexer = indexer;
    private readonly ILanguageServerConfiguration _configuration = configuration;

    public DefinitionRegistrationOptions GetRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc")
        };
    }

    public async Task<DefinitionResult> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var currentFilePath = uri.GetFileSystemPath();
        var userDumpPath = _configuration.GetValue<string>("gsclsp:dumpPath");

        if (!File.Exists(currentFilePath)) return new DefinitionResult();
        var lines = await File.ReadAllLinesAsync(currentFilePath, cancellationToken);
        if (request.Position.Line >= lines.Length) return new DefinitionResult();

        var line = lines[request.Position.Line];
        string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character);

        if (string.IsNullOrEmpty(identifier)) return new DefinitionResult();

        string lookupName = identifier;
        string? pathFilter = null;
        if (identifier.Contains("::"))
        {
            var parts = identifier.Split("::");
            pathFilter = parts[0].Replace("\\", "/").ToLower();
            lookupName = parts[1];
        }
        else if (identifier.StartsWith("::"))
        {
            lookupName = identifier[2..];
        }

        var resolution = _indexer.ResolveFunction(currentFilePath, lookupName);
        var symbol = resolution.Symbol;

        // If we have a pathFilter, override the symbol with one from that path
        if (symbol != null && !string.IsNullOrEmpty(pathFilter))
        {
            symbol = _indexer.GetSymbolsByName(lookupName)
                .FirstOrDefault(s => s.FilePath.Replace("\\", "/").Contains(pathFilter, StringComparison.CurrentCultureIgnoreCase));
        }

        if (symbol != null && symbol.FilePath != "Engine")
        {
            string targetPath = symbol.FilePath;

            if (resolution.Type == ResolutionType.Local && string.IsNullOrEmpty(pathFilter))
            {
                targetPath = currentFilePath;
            }
            else if (!Path.IsPathRooted(targetPath) && !string.IsNullOrEmpty(userDumpPath))
            {
                string cleanDump = userDumpPath.Replace("file:///", "").Replace("/", "\\").TrimEnd('\\');
                targetPath = Path.Combine(cleanDump, targetPath.Replace("/", "\\"));
            }

            if (File.Exists(targetPath))
            {
                var lineIndex = Math.Max(0, symbol.LineNumber - 1);
                return new DefinitionResult(new Location
                {
                    Uri = DocumentUri.FromFileSystemPath(targetPath),
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(lineIndex, 0),
                        new Position(lineIndex, lookupName.Length)
                    )
                });
            }
        }

        return new DefinitionResult();
    }
}