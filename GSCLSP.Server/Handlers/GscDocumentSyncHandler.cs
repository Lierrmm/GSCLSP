using MediatR;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using OmniSharp.Extensions.LanguageServer.Protocol.Server.Capabilities;
using System.Collections.Concurrent;

namespace GSCLSP.Server.Handlers;

public class GscDocumentStore
{
    private readonly ConcurrentDictionary<DocumentUri, string> _documents = new();

    public void Update(DocumentUri uri, string text) => _documents[uri] = text;
    public void Remove(DocumentUri uri) => _documents.TryRemove(uri, out _);
    public string? Get(DocumentUri uri) => _documents.TryGetValue(uri, out var text) ? text : null;
}

public class GscDocumentSyncHandler(GscDocumentStore store) : TextDocumentSyncHandlerBase
{
    private readonly GscDocumentStore _store = store;

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) =>
        new(uri, "gsc");

    public override Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _store.Update(request.TextDocument.Uri, request.TextDocument.Text);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        foreach (var change in request.ContentChanges)
        {
            if (change.Range == null)
                _store.Update(request.TextDocument.Uri, change.Text);
        }
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _store.Remove(request.TextDocument.Uri);
        return Unit.Task;
    }

    public override Task<Unit> Handle(DidSaveTextDocumentParams request, CancellationToken cancellationToken) =>
        Unit.Task;

    protected override TextDocumentSyncRegistrationOptions CreateRegistrationOptions(TextSynchronizationCapability capability, ClientCapabilities clientCapabilities)
    {
        return new TextDocumentSyncRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh"),
            Change = TextDocumentSyncKind.Full,
            Save = new SaveOptions { IncludeText = false }
        };
    }
}
