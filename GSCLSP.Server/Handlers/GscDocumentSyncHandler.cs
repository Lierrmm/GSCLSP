using GSCLSP.Core.Models;
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
    private sealed record DocumentSnapshot(string Text, bool Compiled);

    private readonly ConcurrentDictionary<DocumentUri, DocumentSnapshot> _documents = new();

    public void Update(DocumentUri uri, string text) =>
        _documents[uri] = new DocumentSnapshot(text, GscCompiledScriptDetector.IsCompiledText(text));

    public void Remove(DocumentUri uri) => _documents.TryRemove(uri, out _);

    public string? Get(DocumentUri uri) => _documents.TryGetValue(uri, out var doc) ? doc.Text : null;
    public bool IsCompiled(DocumentUri uri) => _documents.TryGetValue(uri, out var doc) && doc.Compiled;
    public IEnumerable<KeyValuePair<DocumentUri, string>> OpenDocuments =>
        _documents.Select(kv => new KeyValuePair<DocumentUri, string>(kv.Key, kv.Value.Text));
}

public class GscDocumentSyncHandler(GscDocumentStore store, Lazy<GscDiagnosticsHandler> diagnosticsHandler) : TextDocumentSyncHandlerBase
{
    private readonly GscDocumentStore _store = store;
    private readonly Lazy<GscDiagnosticsHandler> _diagnosticsHandler = diagnosticsHandler;

    public override TextDocumentAttributes GetTextDocumentAttributes(DocumentUri uri) =>
        new(uri, "gsc");

    public override async Task<Unit> Handle(DidOpenTextDocumentParams request, CancellationToken cancellationToken)
    {
        _store.Update(request.TextDocument.Uri, request.TextDocument.Text);
        await _diagnosticsHandler.Value.PublishAsync(request.TextDocument.Uri, request.TextDocument.Text, cancellationToken);
        return Unit.Value;
    }

    public override async Task<Unit> Handle(DidChangeTextDocumentParams request, CancellationToken cancellationToken)
    {
        foreach (var change in request.ContentChanges)
        {
            if (change.Range == null)
            {
                _store.Update(request.TextDocument.Uri, change.Text);
                await _diagnosticsHandler.Value.PublishAsync(request.TextDocument.Uri, change.Text, cancellationToken);
            }
        }
        return Unit.Value;
    }

    public override async Task<Unit> Handle(DidCloseTextDocumentParams request, CancellationToken cancellationToken)
    {
        _store.Remove(request.TextDocument.Uri);
        await _diagnosticsHandler.Value.PublishFromDiskAsync(request.TextDocument.Uri, cancellationToken);
        return Unit.Value;
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
