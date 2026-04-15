using GSCLSP.Core.Indexing;
using GSCLSP.Lexer;
using Microsoft.Extensions.Configuration;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using ProtocolRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;

namespace GSCLSP.Server.Handlers;

public class GscRenameHandler(GscIndexer indexer, GscDocumentStore documentStore, IConfiguration? configuration = null)
    : IRenameHandler, IPrepareRenameHandler
{
    private readonly GscIndexer _indexer = indexer;
    private readonly GscDocumentStore _documentStore = documentStore;
    private readonly IConfiguration? _configuration = configuration;

    public RenameRegistrationOptions GetRegistrationOptions(RenameCapability capability, ClientCapabilities clientCapabilities)
    {
        return new RenameRegistrationOptions
        {
            DocumentSelector = TextDocumentSelector.ForLanguage("gsc", "gsh"),
            PrepareProvider = true
        };
    }

    public Task<RangeOrPlaceholderRange?> Handle(PrepareRenameParams request, CancellationToken cancellationToken)
    {
        var target = ResolveTarget(request.TextDocument.Uri, request.Position);
        if (target is null)
            return Task.FromResult<RangeOrPlaceholderRange?>(null);

        var range = new ProtocolRange(
            new Position(target.TriggerLine, target.TriggerStartCol),
            new Position(target.TriggerLine, target.TriggerEndCol));

        return Task.FromResult<RangeOrPlaceholderRange?>(new RangeOrPlaceholderRange(range));
    }

    public Task<WorkspaceEdit?> Handle(RenameParams request, CancellationToken cancellationToken)
    {
        var target = ResolveTarget(request.TextDocument.Uri, request.Position);
        if (target is null)
            return Task.FromResult<WorkspaceEdit?>(null);

        if (!IsValidIdentifier(request.NewName))
            return Task.FromResult<WorkspaceEdit?>(null);

        var changes = new Dictionary<DocumentUri, IEnumerable<TextEdit>>();

        switch (target.Kind)
        {
            case RenameKind.Function:
                CollectFunctionEdits(target.Name, request.NewName, changes, cancellationToken);
                break;
            case RenameKind.GlobalVariable:
                CollectFileWideIdentifierEdits(request.TextDocument.Uri, target.Name, request.NewName, changes);
                break;
            case RenameKind.Macro:
                if (target.MacroScope == MacroScope.LocalFileOnly)
                    CollectFileWideIdentifierEdits(request.TextDocument.Uri, target.Name, request.NewName, changes);
                else
                    CollectWorkspaceMacroEdits(target.Name, request.NewName, changes, cancellationToken);
                break;
            case RenameKind.LocalVariable:
                CollectLocalVariableEdits(request.TextDocument.Uri, target, request.NewName, changes);
                break;
        }

        if (changes.Count == 0)
            return Task.FromResult<WorkspaceEdit?>(null);

        return Task.FromResult<WorkspaceEdit?>(new WorkspaceEdit { Changes = changes });
    }

    private RenameTarget? ResolveTarget(DocumentUri uri, Position position)
    {
        var currentFilePath = uri.GetFileSystemPath();
        var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(currentFilePath);
        if (string.IsNullOrEmpty(content)) return null;

        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (position.Line >= lines.Length) return null;

        var lexed = GscLexingHelper.Lex(content);
        var t = FindIdentifierToken(lexed.Tokens, lines, position);
        if (t is null) return null;

        var token = t.Value;
        var name = token.Text;
        if (string.IsNullOrEmpty(name)) return null;

        var line = lines[token.Line];

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#define", StringComparison.Ordinal))
        {
            var afterDefine = trimmed[7..].TrimStart();
            if (afterDefine.StartsWith(name, StringComparison.Ordinal))
            {
                return new RenameTarget(RenameKind.Macro, name, token.Line, token.Column, token.Column + token.Length, null, MacroScope.LocalFileOnly);
            }
        }

        var fileMacros = GscIndexer.GetFileMacros(currentFilePath);
        if (fileMacros.Any(m => m.Name.Equals(name, StringComparison.Ordinal)))
        {
            return new RenameTarget(RenameKind.Macro, name, token.Line, token.Column, token.Column + token.Length, null, MacroScope.LocalFileOnly);
        }

        var externalMacro = _indexer.ResolveMacro(currentFilePath, name);
        if (externalMacro != null)
        {
            return new RenameTarget(RenameKind.Macro, name, token.Line, token.Column, token.Column + token.Length, null, MacroScope.Workspace);
        }

        if (IsFunctionReference(lexed.Tokens, token))
        {
            return new RenameTarget(RenameKind.Function, name, token.Line, token.Column, token.Column + token.Length, null, MacroScope.LocalFileOnly);
        }

        var funcRange = FindEnclosingFunctionBodyRange(lines, token.Line);
        if (funcRange is not null)
        {
            return new RenameTarget(RenameKind.LocalVariable, name, token.Line, token.Column, token.Column + token.Length, funcRange, MacroScope.LocalFileOnly);
        }

        if (IsTopLevelAssignmentTarget(line, token.Column, name))
        {
            return new RenameTarget(RenameKind.GlobalVariable, name, token.Line, token.Column, token.Column + token.Length, null, MacroScope.LocalFileOnly);
        }

        return null;
    }

    private static Token? FindIdentifierToken(IReadOnlyList<Token> tokens, string[] lines, Position position)
    {
        var candidate = GscLexingHelper.GetTokenAtOrBeforePosition(tokens, position.Line, position.Character);
        if (candidate is { Kind: TokenKind.Identifier or TokenKind.Keyword })
            return candidate;

        if (position.Line >= lines.Length) return null;
        var line = lines[position.Line];
        if (line.Length == 0) return null;

        var trimmed = line.TrimStart();
        if (trimmed.StartsWith("#define", StringComparison.Ordinal))
        {
            int leading = line.Length - trimmed.Length;
            int cursor = Math.Max(0, position.Character - leading);
            var afterDefine = trimmed[7..];
            int nameStartInTrimmed = 7 + (afterDefine.Length - afterDefine.TrimStart().Length);
            int nameEndInTrimmed = nameStartInTrimmed;
            while (nameEndInTrimmed < trimmed.Length && IsIdentifierChar(trimmed[nameEndInTrimmed]))
                nameEndInTrimmed++;

            if (nameEndInTrimmed > nameStartInTrimmed)
            {
                int col = leading + nameStartInTrimmed;
                int len = nameEndInTrimmed - nameStartInTrimmed;
                if (cursor >= nameStartInTrimmed && cursor <= nameEndInTrimmed)
                {
                    var text = trimmed.Substring(nameStartInTrimmed, len);
                    if (char.IsLetter(text[0]) || text[0] == '_')
                        return new Token(TokenKind.Identifier, text, 0, len, position.Line, col);
                }
            }
        }

        int ch = Math.Clamp(position.Character, 0, line.Length);
        int start = ch;
        while (start > 0 && IsIdentifierChar(line[start - 1])) start--;
        int end = ch;
        while (end < line.Length && IsIdentifierChar(line[end])) end++;

        if (end <= start) return null;
        if (!(char.IsLetter(line[start]) || line[start] == '_')) return null;

        foreach (var tok in tokens)
        {
            if (tok.Line != position.Line) continue;
            if (tok.Kind is not TokenKind.Identifier and not TokenKind.Keyword) continue;
            if (tok.Column == start && tok.Length == end - start) return tok;
        }

        return null;
    }

    private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    private static bool TryGetDefineNameSpan(Token token, out int column, out int length, out string name)
    {
        column = 0;
        length = 0;
        name = string.Empty;

        var text = token.Text;
        if (string.IsNullOrEmpty(text)) return false;

        var trimStart = text.TrimStart();
        int leadingWs = text.Length - trimStart.Length;
        if (!trimStart.StartsWith("#define", StringComparison.Ordinal)) return false;

        var afterDefine = trimStart[7..];
        int afterDefineWs = afterDefine.Length - afterDefine.TrimStart().Length;
        int nameStartInText = leadingWs + 7 + afterDefineWs;
        int nameEndInText = nameStartInText;
        while (nameEndInText < text.Length && (char.IsLetterOrDigit(text[nameEndInText]) || text[nameEndInText] == '_'))
            nameEndInText++;

        if (nameEndInText <= nameStartInText) return false;
        if (!(char.IsLetter(text[nameStartInText]) || text[nameStartInText] == '_')) return false;

        column = token.Column + nameStartInText;
        length = nameEndInText - nameStartInText;
        name = text.Substring(nameStartInText, length);
        return true;
    }

    private static bool IsFunctionReference(IReadOnlyList<Token> tokens, Token target)
    {
        Token? next = null;
        foreach (var tok in tokens)
        {
            if (tok.Start <= target.Start) continue;
            if (tok.Kind is TokenKind.Whitespace or TokenKind.Comment) continue;
            next = tok;
            break;
        }

        return next is { Kind: TokenKind.OpenParen };
    }

    private static (int BraceStartLine, int BraceEndLine)? FindEnclosingFunctionBodyRange(string[] lines, int cursorLine)
    {
        int funcDefLine = -1;
        for (int i = cursorLine; i >= 0; i--)
        {
            var ln = lines[i];
            if (ln.Length == 0) continue;
            if (char.IsWhiteSpace(ln[0])) continue;
            if (ln.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
            if (ln.Contains(';')) continue;

            if (System.Text.RegularExpressions.Regex.IsMatch(ln, @"^\w[\w:]*\s*\("))
            {
                funcDefLine = i;
                break;
            }
        }
        if (funcDefLine < 0) return null;

        int braceStart = -1;
        for (int i = funcDefLine; i < lines.Length; i++)
        {
            if (lines[i].Contains('{')) { braceStart = i; break; }
        }
        if (braceStart < 0) return null;

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

        if (cursorLine < funcDefLine || cursorLine > braceEnd) return null;
        return (funcDefLine, braceEnd);
    }

    private static bool IsTopLevelAssignmentTarget(string line, int column, string name)
    {
        if (column != 0 && (column > line.Length || !line[..column].All(char.IsWhiteSpace)))
            return false;

        int tail = column + name.Length;
        if (tail > line.Length) return false;

        var after = line[tail..].TrimStart();
        return after.StartsWith('=') && !after.StartsWith("==", StringComparison.Ordinal);
    }

    private void CollectFunctionEdits(string oldName, string newName, Dictionary<DocumentUri, IEnumerable<TextEdit>> changes, CancellationToken cancellationToken)
    {
        var rawDumpPath = _indexer.DumpPath ?? _configuration?.GetValue<string>("gsclsp:dumpPath");
        var normalizedDumpPath = GscIndexer.NormalizePath(rawDumpPath);

        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _indexer.GetAllIndexedFilePaths())
        {
            if (!visited.Add(path)) continue;
            cancellationToken.ThrowIfCancellationRequested();
            AddIdentifierEdits(path, _indexer.GetFileContent(path), oldName, newName, changes);
        }

        if (!string.IsNullOrEmpty(normalizedDumpPath) && Directory.Exists(normalizedDumpPath))
        {
            foreach (var file in Directory.EnumerateFiles(normalizedDumpPath, "*.*", SearchOption.AllDirectories))
            {
                if (!file.EndsWith(".gsc", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".gsh", StringComparison.OrdinalIgnoreCase) &&
                    !file.EndsWith(".csc", StringComparison.OrdinalIgnoreCase))
                    continue;

                if (!visited.Add(file)) continue;
                cancellationToken.ThrowIfCancellationRequested();
                AddIdentifierEdits(file, _indexer.GetFileContent(file), oldName, newName, changes);
            }
        }
    }

    private void CollectWorkspaceMacroEdits(string oldName, string newName, Dictionary<DocumentUri, IEnumerable<TextEdit>> changes, CancellationToken cancellationToken)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var path in _indexer.GetAllIndexedFilePaths())
        {
            if (!visited.Add(path)) continue;
            cancellationToken.ThrowIfCancellationRequested();

            var uri = DocumentUri.FromFileSystemPath(path);
            var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(path);
            if (string.IsNullOrEmpty(content)) continue;

            AddIdentifierEditsFromContent(uri, content, oldName, newName, changes);
        }
    }

    private void CollectFileWideIdentifierEdits(DocumentUri uri, string oldName, string newName, Dictionary<DocumentUri, IEnumerable<TextEdit>> changes)
    {
        var path = uri.GetFileSystemPath();
        var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(path);
        if (string.IsNullOrEmpty(content)) return;

        AddIdentifierEditsFromContent(uri, content, oldName, newName, changes);
    }

    private void CollectLocalVariableEdits(DocumentUri uri, RenameTarget target, string newName, Dictionary<DocumentUri, IEnumerable<TextEdit>> changes)
    {
        if (target.FunctionBodyRange is null) return;

        var path = uri.GetFileSystemPath();
        var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(path);
        if (string.IsNullOrEmpty(content)) return;

        var lexed = GscLexingHelper.Lex(content);
        var edits = new List<TextEdit>();
        var (start, end) = target.FunctionBodyRange.Value;

        foreach (var token in lexed.Tokens)
        {
            if (token.Kind is not TokenKind.Identifier and not TokenKind.Keyword) continue;
            if (token.Line < start || token.Line > end) continue;
            if (!token.Text.Equals(target.Name, StringComparison.Ordinal)) continue;

            edits.Add(new TextEdit
            {
                Range = new ProtocolRange(
                    new Position(token.Line, token.Column),
                    new Position(token.Line, token.Column + token.Length)),
                NewText = newName
            });
        }

        if (edits.Count > 0) changes[uri] = edits;
    }

    private void AddIdentifierEdits(string filePath, string content, string oldName, string newName, Dictionary<DocumentUri, IEnumerable<TextEdit>> changes)
    {
        if (string.IsNullOrEmpty(content)) return;
        AddIdentifierEditsFromContent(DocumentUri.FromFileSystemPath(filePath), content, oldName, newName, changes);
    }

    private static void AddIdentifierEditsFromContent(DocumentUri uri, string content, string oldName, string newName, Dictionary<DocumentUri, IEnumerable<TextEdit>> changes)
    {
        if (!content.Contains(oldName, StringComparison.Ordinal)) return;

        var lexed = GscLexingHelper.Lex(content);
        var edits = new List<TextEdit>();

        foreach (var token in lexed.Tokens)
        {
            if (token.Kind is TokenKind.Directive)
            {
                if (TryGetDefineNameSpan(token, out var defineCol, out var defineLen, out var defineName) &&
                    defineName.Equals(oldName, StringComparison.Ordinal))
                {
                    edits.Add(new TextEdit
                    {
                        Range = new ProtocolRange(
                            new Position(token.Line, defineCol),
                            new Position(token.Line, defineCol + defineLen)),
                        NewText = newName
                    });
                }
                continue;
            }

            if (token.Kind is not TokenKind.Identifier and not TokenKind.Keyword) continue;
            if (!token.Text.Equals(oldName, StringComparison.Ordinal)) continue;

            edits.Add(new TextEdit
            {
                Range = new ProtocolRange(
                    new Position(token.Line, token.Column),
                    new Position(token.Line, token.Column + token.Length)),
                NewText = newName
            });
        }

        if (edits.Count > 0) changes[uri] = edits;
    }

    private static bool IsValidIdentifier(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (!(char.IsLetter(name[0]) || name[0] == '_')) return false;
        for (int i = 1; i < name.Length; i++)
        {
            var c = name[i];
            if (!(char.IsLetterOrDigit(c) || c == '_')) return false;
        }
        return true;
    }

    private enum RenameKind { Function, GlobalVariable, LocalVariable, Macro }

    private enum MacroScope { LocalFileOnly, Workspace }

    private sealed record RenameTarget(
        RenameKind Kind,
        string Name,
        int TriggerLine,
        int TriggerStartCol,
        int TriggerEndCol,
        (int BraceStartLine, int BraceEndLine)? FunctionBodyRange,
        MacroScope MacroScope);
}
