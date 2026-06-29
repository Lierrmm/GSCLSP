using GSCLSP.Core.Formatting;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ProtocolRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCLSP.Server.Handlers;

public class GscFormattingHandler(GscDocumentStore documentStore)
    : IDocumentFormattingHandler
{
    private readonly GscDocumentStore _documentStore = documentStore;

    public DocumentFormattingRegistrationOptions GetRegistrationOptions(
        DocumentFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh")
        };
    }

    public Task<TextEditContainer?> Handle(DocumentFormattingParams request, CancellationToken cancellationToken)
    {
        var content = _documentStore.Get(request.TextDocument.Uri);
        if (string.IsNullOrEmpty(content))
            return Task.FromResult<TextEditContainer?>(null);

        var formatted = GscFormatter.Format(
            content,
            request.Options.InsertSpaces,
            request.Options.TabSize);

        if (formatted == content)
            return Task.FromResult<TextEditContainer?>(new TextEditContainer());

        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var lastLine = lines.Length - 1;
        var lastCol = lines[lastLine].Length;

        var edit = new TextEdit
        {
            Range = new ProtocolRange(new Position(0, 0), new Position(lastLine, lastCol)),
            NewText = formatted
        };

        return Task.FromResult<TextEditContainer?>(new TextEditContainer(edit));
    }
}

public class GscRangeFormattingHandler(GscDocumentStore documentStore)
    : IDocumentRangeFormattingHandler
{
    private readonly GscDocumentStore _documentStore = documentStore;

    public DocumentRangeFormattingRegistrationOptions GetRegistrationOptions(
        DocumentRangeFormattingCapability capability, ClientCapabilities clientCapabilities)
    {
        return new DocumentRangeFormattingRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh")
        };
    }

    public Task<TextEditContainer> Handle(DocumentRangeFormattingParams request, CancellationToken cancellationToken)
    {
        var content = _documentStore.Get(request.TextDocument.Uri);
        if (string.IsNullOrEmpty(content))
            return Task.FromResult<TextEditContainer>(new TextEditContainer());

        var startLine = request.Range.Start.Line;
        var endLine = request.Range.End.Line;

        if (endLine > startLine && request.Range.End.Character == 0)
            endLine--;

        var formatted = GscFormatter.FormatRange(
            content,
            startLine,
            endLine,
            request.Options.InsertSpaces,
            request.Options.TabSize);

        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        endLine = Math.Min(endLine, lines.Length - 1);
        var lastCol = lines[endLine].Length;

        var edit = new TextEdit
        {
            Range = new ProtocolRange(
                new Position(startLine, 0),
                new Position(endLine, lastCol)),
            NewText = formatted
        };

        return Task.FromResult<TextEditContainer>(new TextEditContainer(edit));
    }
}
