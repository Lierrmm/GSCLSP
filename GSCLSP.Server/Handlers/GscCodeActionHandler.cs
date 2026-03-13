using GSCLSP.Core.Parsing;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server.Handlers;

public class GscCodeActionHandler(GscDocumentStore documentStore, GscDiagnosticsHandler diagnosticsHandler) : ICodeActionHandler
{
    private readonly GscDocumentStore _documentStore = documentStore;
    private readonly GscDiagnosticsHandler _diagnosticsHandler = diagnosticsHandler;

    public async Task<CommandOrCodeActionContainer> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var text = _documentStore.Get(uri);
        if (string.IsNullOrEmpty(text))
            return new CommandOrCodeActionContainer();

        var actions = new List<CommandOrCodeAction>();
        var currentFilePath = uri.GetFileSystemPath();
        var existingIncludes = GetExistingIncludes(text);
        var candidates = GetFunctionCandidates(request, text);

        foreach (var candidate in candidates)
        {
            var suggestedIncludes = await _diagnosticsHandler.GetSuggestedIncludesAsync(currentFilePath, text, candidate.FunctionName, cancellationToken);
            foreach (var includePath in suggestedIncludes)
            {
                if (!existingIncludes.Add(includePath))
                    continue;

                actions.Add(new CommandOrCodeAction(CreateIncludeCodeAction(uri, candidate.Diagnostic, text, includePath, candidate.FunctionName)));
            }
        }

        return new CommandOrCodeActionContainer(actions);
    }

    public CodeActionRegistrationOptions GetRegistrationOptions(CodeActionCapability capability, ClientCapabilities clientCapabilities)
    {
        return new CodeActionRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh"),
            CodeActionKinds = new Container<CodeActionKind>(CodeActionKind.QuickFix)
        };
    }

    public void SetCapability(CodeActionCapability capability)
    {
    }

    private static List<FunctionCandidate> GetFunctionCandidates(CodeActionParams request, string text)
    {
        var candidates = new List<FunctionCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var diagnostic in request.Context.Diagnostics.Where(IsUnresolvedFunctionDiagnostic))
        {
            var functionName = GetFunctionName(diagnostic, text);
            if (string.IsNullOrWhiteSpace(functionName) || !seen.Add(functionName))
                continue;

            candidates.Add(new FunctionCandidate(functionName, diagnostic));
        }

        if (candidates.Count > 0)
            return candidates;

        var fallback = GetFunctionNameFromRequestRange(text, request.Range);
        if (!string.IsNullOrWhiteSpace(fallback))
        {
            candidates.Add(new FunctionCandidate(fallback, new Diagnostic { Range = request.Range }));
        }

        return candidates;
    }

    private static bool IsUnresolvedFunctionDiagnostic(Diagnostic diagnostic)
    {
        if (!string.Equals(diagnostic.Source, "gsclsp", StringComparison.OrdinalIgnoreCase))
            return false;

        if (string.Equals(diagnostic.Code?.ToString(), GscDiagnosticsHandler.UnresolvedFunctionDiagnosticCode, StringComparison.OrdinalIgnoreCase))
            return true;

        return diagnostic.Message.Contains("not defined in this file or its included files", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetFunctionName(Diagnostic diagnostic, string text)
    {
        var dataValue = diagnostic.Data?.ToString();
        if (!string.IsNullOrWhiteSpace(dataValue))
            return dataValue.Trim('"');

        return GetTextInRange(text, diagnostic.Range);
    }

    private static string GetFunctionNameFromRequestRange(string text, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range)
    {
        var exact = GetTextInRange(text, range);
        if (!string.IsNullOrWhiteSpace(exact))
            return exact;

        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (range.Start.Line >= lines.Length)
            return string.Empty;

        return GscWordScanner.GetFullIdentifierAt(lines[range.Start.Line], range.Start.Character).TrimStart(':');
    }

    private static CodeAction CreateIncludeCodeAction(DocumentUri uri, Diagnostic diagnostic, string text, string includePath, string functionName)
    {
        var insertLine = GetIncludeInsertLine(text);
        var insertText = $"#include {includePath};{Environment.NewLine}{Environment.NewLine}";

        return new CodeAction
        {
            Title = $"Add '#include {includePath}' for '{functionName}'",
            Kind = CodeActionKind.QuickFix,
            IsPreferred = true,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [uri] =
                    [
                        new TextEdit
                        {
                            Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                                new Position(insertLine, 0),
                                new Position(insertLine, 0)),
                            NewText = insertText
                        }
                    ]
                }
            }
        };
    }

    private static HashSet<string> GetExistingIncludes(string text)
    {
        var includes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (!trimmed.StartsWith("#include") && !trimmed.StartsWith("#using"))
                continue;

            var parts = trimmed.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2)
                continue;

            includes.Add(parts[1].Trim().TrimEnd(';'));
        }

        return includes;
    }

    private static int GetIncludeInsertLine(string text)
    {
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var insertLine = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.StartsWith("#include") || trimmed.StartsWith("#using") || string.IsNullOrWhiteSpace(trimmed))
            {
                insertLine = i + 1;
                continue;
            }

            break;
        }

        return insertLine;
    }

    private static string GetTextInRange(string text, OmniSharp.Extensions.LanguageServer.Protocol.Models.Range range)
    {
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (range.Start.Line >= lines.Length || range.End.Line >= lines.Length)
            return string.Empty;

        if (range.Start.Line != range.End.Line)
            return string.Empty;

        var line = lines[range.Start.Line];
        if (range.Start.Character < 0 || range.End.Character > line.Length || range.Start.Character >= range.End.Character)
            return string.Empty;

        return line[range.Start.Character..range.End.Character];
    }

    private sealed record FunctionCandidate(string FunctionName, Diagnostic Diagnostic);
}