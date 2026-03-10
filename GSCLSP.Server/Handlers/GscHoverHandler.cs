using GSCLSP.Core.Indexing;
using GSCLSP.Core.Parsing;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

public partial class GscHoverHandler(GscIndexer indexer) : IHoverHandler
{
    private readonly GscIndexer _indexer = indexer;

    public HoverRegistrationOptions GetRegistrationOptions(HoverCapability capability, ClientCapabilities clientCapabilities)
    {
        return new HoverRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh")
        };
    }

    public async Task<Hover?> Handle(HoverParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var filePath = uri.GetFileSystemPath();

        if (!File.Exists(filePath)) return null;
        var lines = await File.ReadAllLinesAsync(filePath, cancellationToken);
        if (lines == null || request.Position.Line >= lines.Length) return null;

        var line = lines[request.Position.Line];

        bool inBlockComment = false;
        for (int l = 0; l < request.Position.Line; l++)
            GscHandlerCommon.GetCodeRanges(lines[l], ref inBlockComment);
        var codeRanges = GscHandlerCommon.GetCodeRanges(line, ref inBlockComment);
        if (!GscHandlerCommon.IsInCode(codeRanges, request.Position.Character)) return null;

        if (line.Trim().StartsWith("#include") || line.Trim().StartsWith("#using") || line.Trim().StartsWith("#inline"))
        {
            string includedFile = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character);
            if (string.IsNullOrEmpty(includedFile)) return null;

            var foundIncludePath = await _indexer.GetIncludePath(includedFile);
            if (foundIncludePath == null) return null;

            var contentValue = $"### #Include\n`{foundIncludePath}`";
            var content = new MarkupContent { Kind = MarkupKind.Markdown, Value = contentValue };
            return new Hover { Contents = new MarkedStringsOrMarkupContent(content) };
        }

        string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character).Trim();
        if (identifier.StartsWith("::")) identifier = identifier[2..];
        if (string.IsNullOrEmpty(identifier)) return null;

        var resolution = _indexer.ResolveFunction(filePath, identifier);

        if (resolution.Symbol != null)
        {
            var symbol = resolution.Symbol;

            // If we have a file path but no documentation, go get it from the source definition
            if (string.IsNullOrEmpty(symbol.Documentation) && symbol.FilePath != "Engine" && File.Exists(symbol.FilePath))
            {
                // We use our strict scanner to find the actual definition and its ScriptDoc
                var detailedSymbol = GscIndexer.ScanFileForFunction(symbol.FilePath, symbol.Name);
                if (detailedSymbol != null)
                {
                    symbol = detailedSymbol;
                }
            }

            var signature = $"{symbol.Name}( {symbol.Parameters} )";
            var contentValue = $"```gsc\n{signature}\n```\n";

            if (!string.IsNullOrEmpty(symbol.Documentation))
            {
                var doc = DocRegex().Replace(symbol.Documentation, "**$1:**");
                contentValue += $"{doc}\n\n";
            }

            contentValue += "---\n";

            if (symbol.FilePath == "Engine")
            {
                contentValue += "*(Engine Built-in)*";
            }
            else
            {
                // Backticks around file path to prevent backslash escaping
                contentValue += $"**Defined in:** `{symbol.FilePath}`\n\n" +
                             $"**Line:** {symbol.LineNumber}";
            }

            var content = new MarkupContent { Kind = MarkupKind.Markdown, Value = contentValue };
            return new Hover { Contents = new MarkedStringsOrMarkupContent(content) };
        }

        return null;
    }
}
