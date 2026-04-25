using GSCLSP.Core.Diagnostics;
using GSCLSP.Core.Indexing;
using GSCLSP.Core.Parsing;
using GSCLSP.Lexer;
using OmniSharp.Extensions.LanguageServer.Protocol.Client.Capabilities;
using OmniSharp.Extensions.LanguageServer.Protocol.Document;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using static GSCLSP.Core.Indexing.GscIndexer;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

public partial class GscHoverHandler(GscIndexer indexer, GscDocumentStore documentStore) : IHoverHandler
{
    private readonly GscIndexer _indexer = indexer;
    private readonly GscDocumentStore _documentStore = documentStore;

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

        var content = _documentStore.Get(uri) ?? _indexer.GetFileContent(filePath);
        var lines = content.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        if (request.Position.Line >= lines.Length) return null;

        var line = lines[request.Position.Line];

        if (GscHandlerCommon.IsIncludeLikeDirective(line.Trim()) &&
            GscHandlerCommon.TryExtractDirectivePath(line, out var includedFile))
        {
            var parts = line.Trim().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 1) return null;

            var directive = parts[0];
            var foundIncludePath = await _indexer.GetIncludePathAsync(includedFile);
            if (foundIncludePath == null) return null;

            var contentValue = $"### {directive}\n`{foundIncludePath}`";
            var markupContent = new MarkupContent { Kind = MarkupKind.Markdown, Value = contentValue };
            return new Hover { Contents = new MarkedStringsOrMarkupContent(markupContent) };
        }

        var lexed = GscLexingHelper.Lex(content);
        var token = GscLexingHelper.GetTokenAtOrBeforePosition(lexed.Tokens, request.Position.Line, request.Position.Character);

        var lineTrimmed = line.TrimStart();
        var isOnMacroDefinition = lineTrimmed.StartsWith("#define", StringComparison.Ordinal);

        if (!isOnMacroDefinition && (token is null || !IsHoverableToken(token.Value)))
            return null;

        string identifier = GscWordScanner.GetFullIdentifierAt(line, request.Position.Character).Trim();
        if (identifier.StartsWith("::")) identifier = identifier[2..];

        if (string.IsNullOrEmpty(identifier) && isOnMacroDefinition)
        {
            identifier = ExtractMacroNameAtCursor(line, request.Position.Character);
        }

        if (string.IsNullOrEmpty(identifier)) return null;

        var macroHover = FindMacroDefinition(filePath, identifier);
        if (macroHover != null) return macroHover;

        var localVarHover = FindLocalVariable(filePath, lines, identifier, request.Position.Line);
        if (localVarHover != null) return localVarHover;

        var resolution = _indexer.ResolveFunction(filePath, identifier);

        if (resolution.Symbol != null)
        {
            var symbol = resolution.Symbol;

            // If we have a file path but no documentation, go get it from the source definition
            if (string.IsNullOrEmpty(symbol.Documentation) && symbol.FilePath != "Engine")
            {
                // We use our strict scanner to find the actual definition and its ScriptDoc
                var detailedSymbol = GscIndexer.ScanFileForFunction(symbol.FilePath, symbol.Name);
                if (detailedSymbol != null)
                {
                    symbol = detailedSymbol;
                }
            }

            var signature = GscCompletionItemFactory.GetSignatureText(symbol);

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

            var markupContent = new MarkupContent { Kind = MarkupKind.Markdown, Value = contentValue };
            return new Hover { Contents = new MarkedStringsOrMarkupContent(markupContent) };
        }

        return null;
    }

    private static string ExtractMacroNameAtCursor(string line, int cursorChar)
    {
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith("#define", StringComparison.Ordinal)) return string.Empty;

        int leading = line.Length - trimmed.Length;
        var afterDefine = trimmed[7..];
        int nameStart = leading + 7 + (afterDefine.Length - afterDefine.TrimStart().Length);
        int nameEnd = nameStart;
        while (nameEnd < line.Length && (char.IsLetterOrDigit(line[nameEnd]) || line[nameEnd] == '_'))
            nameEnd++;

        if (nameEnd <= nameStart) return string.Empty;
        if (cursorChar < nameStart || cursorChar > nameEnd) return string.Empty;
        return line.Substring(nameStart, nameEnd - nameStart);
    }

    private static bool IsHoverableToken(Token token)
    {
        return token.Kind is not TokenKind.Whitespace
            and not TokenKind.Comment
            and not TokenKind.String
            and not TokenKind.Directive
            and not TokenKind.BadToken
            and not TokenKind.EndOfFile;
    }

    private static Hover? FindLocalVariable(string filePath, string[] lines, string identifier, int hoverLine)
    {
        var funcName = GscIndexer.FindEnclosingFunctionName(lines, hoverLine);
        if (funcName == null) return null;

        var locals = GscIndexer.GetLocalVariables(filePath, funcName, lines, hoverLine);
        var matching = locals
            .Where(v => v.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (matching.Count == 0) return null;

        var paramEntry = matching.FirstOrDefault(v => v.Value == "parameter");
        string contentValue;
        List<LocalVariable> reassignments;

        if (paramEntry != null)
        {
            contentValue = $"```gsc\n{paramEntry.Name}\n```\n";
            contentValue += $"---\n`Function parameter`\n\n**Line:** {paramEntry.Line}";
            reassignments = [.. matching.Where(v => v != paramEntry)];
        }
        else
        {
            var localVar = matching[0];
            var comment = GetLeadingComment(lines, localVar.Line - 1);

            contentValue = $"```gsc\n{localVar.Name} = {localVar.Value}\n```\n";
            if (!string.IsNullOrEmpty(comment))
                contentValue += $"{comment}\n\n";
            contentValue += $"---\n`Variable`\n\n**Line:** {localVar.Line}";
            reassignments = [.. matching.Skip(1)];
        }

        if (reassignments.Count > 0)
        {
            contentValue += "\n\n";
            foreach (var r in reassignments)
                contentValue += $"*- value gets reassigned at line {r.Line}*\n\n";
        }

        var markupContent = new MarkupContent { Kind = MarkupKind.Markdown, Value = contentValue };
        return new Hover { Contents = new MarkedStringsOrMarkupContent(markupContent) };
    }

    private static string? GetLeadingComment(string[] lines, int lineIndex)
    {
        if (lineIndex <= 0 || string.IsNullOrWhiteSpace(lines[lineIndex - 1].Trim()))
            return null;

        List<string> commentLines = [];
        bool inBlockComment = false;

        for (int i = lineIndex - 1; i >= 0; i--)
        {
            string prevLine = lines[i].Trim();

            if (prevLine.EndsWith("*/")) { inBlockComment = true; prevLine = prevLine.Replace("*/", ""); }
            if (prevLine.StartsWith("/*"))
            {
                string clean = prevLine.TrimStart('/', '*', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                    commentLines.Insert(0, clean);
                break;
            }

            if (inBlockComment || prevLine.StartsWith("//"))
            {
                string clean = prevLine.TrimStart('/', '*', ' ').Trim();
                if (!string.IsNullOrWhiteSpace(clean))
                    commentLines.Insert(0, clean);
            }
            else break;
        }

        return commentLines.Count > 0 ? string.Join("  \n", commentLines) : null;
    }

    private Hover? FindMacroDefinition(string filePath, string identifier)
    {
        var macro = _indexer.ResolveMacro(filePath, identifier);
        if (macro == null) return null;

        // a preprocessor may have a #ifdef define #else define #endif, which means hover should use the active
        macro = GetActveMacroDefinition(filePath, identifier, macro);

        string[] macroFileLines = _indexer.GetFileLines(macro.FilePath);
        var comment = GetLeadingComment(macroFileLines, macro.Line - 1);

        var contentValue = $"```gsc\n#define {macro.Name}";
        if (!string.IsNullOrEmpty(macro.Value))
            contentValue += $" {macro.Value}";
        contentValue += "\n```\n";
        if (!string.IsNullOrEmpty(comment))
            contentValue += $"{comment}\n\n";
        contentValue += $"---\n**Defined in:** `{macro.FilePath}`\n\n";
        contentValue += $"**Line:** {macro.Line}";

        var content = new MarkupContent { Kind = MarkupKind.Markdown, Value = contentValue };
        return new Hover { Contents = new MarkedStringsOrMarkupContent(content) };
    }

    private MacroDefinition GetActveMacroDefinition(string filePath, string identifier, MacroDefinition fallback)
    {
        var localMatches = GscIndexer.GetFileMacros(filePath)
            .Where(m => m.Name.Equals(identifier, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (localMatches.Count <= 1) return fallback;

        var inactiveRanges = GscInactiveRegionAnalyzer.Analyze(_indexer.GetFileLines(filePath), _indexer.CurrentGame);

        var active = localMatches.FirstOrDefault(m =>
        {
            int zeroBased = m.Line - 1;
            return !inactiveRanges.Any(r => zeroBased >= r.StartLine && zeroBased <= r.EndLine);
        });

        return active ?? fallback;
    }
}
