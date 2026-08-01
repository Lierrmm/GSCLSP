using GSCLSP.Core.Diagnostics;
using GSCLSP.Core.Models;
using GSCLSP.Core.Parsing;
using GSCLSP.Lexer;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace GSCLSP.Server.Handlers;

public class GscCodeActionHandler(GscDocumentStore documentStore, GscDiagnosticsHandler diagnosticsHandler) : ICodeActionHandler
{
    private readonly GscDocumentStore _documentStore = documentStore;
    private readonly GscDiagnosticsHandler _diagnosticsHandler = diagnosticsHandler;

    public async Task<CommandOrCodeActionContainer?> Handle(CodeActionParams request, CancellationToken cancellationToken)
    {
        var uri = request.TextDocument.Uri;
        var text = _documentStore.Get(uri);
        if (string.IsNullOrEmpty(text) || _documentStore.IsCompiled(uri))
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

        var renamedVariables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var diagnostic in request.Context.Diagnostics.Where(IsVariableShadowsNamespaceDiagnostic))
        {
            var name = GetDiagnosticIdentifier(diagnostic, text);
            if (string.IsNullOrWhiteSpace(name) || !renamedVariables.Add(name))
                continue;

            var renameAction = CreateRenameShadowingVariableAction(uri, text, diagnostic, name);
            if (renameAction is not null)
                actions.Add(new CommandOrCodeAction(renameAction));
        }

        return new CommandOrCodeActionContainer(actions);
    }

    private static bool IsVariableShadowsNamespaceDiagnostic(Diagnostic diagnostic)
    {
        return string.Equals(diagnostic.Source, "gsclsp", StringComparison.OrdinalIgnoreCase)
            && string.Equals(diagnostic.Code?.ToString(), GscDiagnosticsAnalyzer.VariableShadowsNamespaceWarningCode, StringComparison.OrdinalIgnoreCase);
    }

    private static CodeAction? CreateRenameShadowingVariableAction(DocumentUri uri, string text, Diagnostic diagnostic, string name)
    {
        var lines = text.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (diagnostic.Range.Start.Line >= lines.Length)
            return null;

        var bodyRange = FindEnclosingFunctionBodyRange(lines, diagnostic.Range.Start.Line);
        if (bodyRange is null)
            return null;

        var newName = $"_{name}";
        var edits = CollectVariableRenameEdits(GscLexingHelper.Lex(text).Tokens, bodyRange.Value, name, newName);
        if (edits.Count == 0)
            return null;

        return new CodeAction
        {
            Title = $"Rename '{name}' to '{newName}'",
            Kind = CodeActionKind.QuickFix,
            IsPreferred = true,
            Diagnostics = new Container<Diagnostic>(diagnostic),
            Edit = new WorkspaceEdit
            {
                Changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>
                {
                    [uri] = edits
                }
            }
        };
    }

    private static List<TextEdit> CollectVariableRenameEdits(
        IReadOnlyList<Token> tokens,
        (int StartLine, int EndLine) bodyRange,
        string name,
        string newName)
    {
        var edits = new List<TextEdit>();

        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Kind is not TokenKind.Identifier and not TokenKind.Keyword)
                continue;

            if (token.Line < bodyRange.StartLine || token.Line > bodyRange.EndLine)
                continue;

            if (!token.Text.Equals(name, StringComparison.Ordinal))
                continue;

            if (GscVariableTokenFilter.IsNamespaceOrMemberUsage(tokens, i))
                continue;

            edits.Add(new TextEdit
            {
                Range = new OmniSharp.Extensions.LanguageServer.Protocol.Models.Range(
                    new Position(token.Line, token.Column),
                    new Position(token.Line, token.Column + token.Length)),
                NewText = newName
            });
        }

        return edits;
    }

    private static (int StartLine, int EndLine)? FindEnclosingFunctionBodyRange(string[] lines, int cursorLine)
    {
        int funcDefLine = -1;
        for (int i = cursorLine; i >= 0; i--)
        {
            var line = lines[i];
            if (line.Length == 0 || char.IsWhiteSpace(line[0]))
                continue;
            if (line.TrimStart().StartsWith("//", StringComparison.Ordinal))
                continue;
            if (line.Contains(';'))
                continue;

            var match = RegexPatterns.FunctionMultiLineRegex().Match(line);
            if (match.Success && match.Index == 0)
            {
                funcDefLine = i;
                break;
            }
        }

        if (funcDefLine < 0)
            return null;

        int braceStart = -1;
        for (int i = funcDefLine; i < lines.Length; i++)
        {
            if (lines[i].Contains('{')) { braceStart = i; break; }
        }
        if (braceStart < 0)
            return null;

        int depth = 0;
        int braceEnd = lines.Length - 1;
        for (int i = braceStart; i < lines.Length; i++)
        {
            foreach (char c in lines[i])
            {
                if (c == '{') depth++;
                else if (c == '}') depth--;
            }
            if (depth == 0) { braceEnd = i; break; }
        }

        if (cursorLine < funcDefLine || cursorLine > braceEnd)
            return null;

        return (funcDefLine, braceEnd);
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
            var functionName = GetDiagnosticIdentifier(diagnostic, text);
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

    private static string GetDiagnosticIdentifier(Diagnostic diagnostic, string text)
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
            if (GscHandlerCommon.TryExtractDirectivePath(line, out var includePath, includeInline: false))
            {
                includes.Add(includePath.TrimEnd(';'));
            }
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
            if (GscHandlerCommon.IsIncludeOrUsingDirective(trimmed) || string.IsNullOrWhiteSpace(trimmed))
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