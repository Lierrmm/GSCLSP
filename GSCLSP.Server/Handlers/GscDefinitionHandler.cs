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

public class GscDefinitionHandler(GscIndexer indexer, GscDocumentStore documentStore, ILanguageServerConfiguration? configuration = null) : IDefinitionHandler
{
    private readonly GscIndexer _indexer = indexer;
    private readonly GscDocumentStore _documentStore = documentStore;
    private readonly ILanguageServerConfiguration? _configuration = configuration;

    public DefinitionRegistrationOptions GetRegistrationOptions(DefinitionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DefinitionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh")
        };
    }

    public async Task<DefinitionResult?> Handle(DefinitionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var currentFilePath = uri.GetFileSystemPath();
        var userDumpPath = _indexer.DumpPath;

        var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(currentFilePath);
        if (string.IsNullOrEmpty(content)) return new DefinitionResult();

        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (request.Position.Line >= lines.Length) return new DefinitionResult();

        var line = lines[request.Position.Line];

        if (GscHandlerCommon.IsIncludeLikeDirective(line.Trim()) &&
            GscHandlerCommon.TryExtractDirectivePath(line, out var includedFile))
        {
            var foundIncludePath = await _indexer.GetIncludePathAsync(includedFile);
            if (foundIncludePath != null)
            {
                return new DefinitionResult(new Location
                {
                    Uri = DocumentUri.FromFileSystemPath(foundIncludePath),
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(0, 0, 0, 0)
                });
            }
        }

        string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character);
        if (string.IsNullOrEmpty(identifier)) return new DefinitionResult();

        var macro = _indexer.ResolveMacro(currentFilePath, identifier);
        if (macro != null)
        {
            int targetLine = macro.Line - 1;
            return new DefinitionResult(new Location
            {
                Uri = DocumentUri.FromFileSystemPath(macro.FilePath),
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(targetLine, 0),
                    new Position(targetLine, 0)
                )
            });
        }

        var funcName = GscIndexer.FindEnclosingFunctionName(lines, request.Position.Line);
        if (funcName != null)
        {
            var locals = GscIndexer.GetLocalVariables(currentFilePath, funcName, lines, request.Position.Line);
            var localVar = locals.FirstOrDefault(v => v.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase));
            if (localVar != null)
            {
                int targetLine = localVar.Line - 1;
                return new DefinitionResult(new Location
                {
                    Uri = uri,
                    Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                        new Position(targetLine, 0),
                        new Position(targetLine, lines[targetLine].Length)
                    )
                });
            }
        }

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
            var allSymbols = _indexer.Symbols.Concat(_indexer.WorkspaceSymbols);

            symbol = allSymbols.FirstOrDefault(s =>
                s.Name.Equals(lookupName, StringComparison.OrdinalIgnoreCase) &&
                s.FilePath.Replace("\\", "/").Contains(pathFilter, StringComparison.OrdinalIgnoreCase));
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

        return new DefinitionResult();
    }
}