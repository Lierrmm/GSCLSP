using System.Text;
using GSCLSP.Lexer;

namespace GSCLSP.Core.Formatting;

internal static class GscRespacer
{
    private static readonly HashSet<string> KeywordsSpaceBeforeParen = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "foreach", "switch", "return"
    };

    private static readonly HashSet<string> NonValueKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "while", "foreach", "switch", "case", "default",
        "return", "thread", "childthread", "wait", "do", "in", "breakpoint",
        "waittill", "waittillmatch", "waittillframeend", "endon", "notify"
    };

    public static List<string> Respace(List<string> rawLines, bool seedInBlock = false)
    {
        var res = new List<string>(rawLines.Count);
        var inBlock = seedInBlock;

        foreach (var rawLine in rawLines)
        {
            var info = LineAnalyzer.Analyze(rawLine, inBlock, false);
            var startedInBlock = inBlock;
            inBlock = info.EndsInBlockComment;

            if (info.IsBlank) { res.Add(""); continue; }
            if (startedInBlock) { res.Add(info.TrimmedEnd); continue; }
            if (info.IsCommentOnly || !info.Respaceable) { res.Add(info.Trimmed); continue; }

            var codeRaw = info.CommentAt >= 0 ? rawLine[..info.CommentAt] : rawLine;
            var code = codeRaw.Trim();
            var respaced = RespaceCode(code);

            if (info.CommentAt >= 0)
            {
                var gap = codeRaw[codeRaw.TrimEnd().Length..];
                var comment = rawLine[info.CommentAt..];
                res.Add(respaced + (respaced.Length > 0 ? (gap.Length > 0 ? gap : " ") : "") + comment);
            }
            else
            {
                res.Add(respaced);
            }
        }

        return res;
    }

    public static string RespaceCode(string code)
    {
        if (code.Length == 0) return "";

        var lexer = new GscLexer();
        LexerResult result;
        try
        {
            result = lexer.Lex(code);
        }
        catch
        {
            return code;
        }

        var tokens = new List<Token>();
        foreach (var t in result.Tokens)
        {
            if (t.Kind is not TokenKind.Whitespace and not TokenKind.Comment and not TokenKind.EndOfFile)
                tokens.Add(t);
        }

        if (tokens.Count == 0) return code;

        var sb = new StringBuilder(code.Length);
        sb.Append(code.AsSpan(0, tokens[0].Start));
        sb.Append(tokens[0].Text);
        var ternaryDepth = 0;

        for (var i = 1; i < tokens.Count; i++)
        {
            var prev = tokens[i - 1];
            var cur = tokens[i];

            if (cur.Kind == TokenKind.Question) ternaryDepth++;
            var curIsTernaryColon = false;
            if (cur.Kind == TokenKind.Colon && ternaryDepth > 0)
            {
                curIsTernaryColon = true;
                ternaryDepth--;
            }

            var wantSpace = WantSpaceBetween(prev, cur, tokens, i, curIsTernaryColon);

            var gapStart = prev.Start + prev.Length;
            var gapEnd = cur.Start;
            var gap = gapEnd > gapStart ? code[gapStart..gapEnd] : "";
            var wsLen = 0;
            while (wsLen < gap.Length && char.IsWhiteSpace(gap[wsLen])) wsLen++;
            var ws = gap[..wsLen];
            var prefix = gap[wsLen..];

            string separator;
            if (prev.Kind == TokenKind.Percent)
            {
                separator = ws;
            }
            else
            {
                var isAlignment = ws.Length >= 2 || ws.Contains('\t');
                separator = wantSpace ? (isAlignment ? ws : " ") : "";
            }

            sb.Append(separator);
            sb.Append(prefix);
            sb.Append(cur.Text);
        }

        return sb.ToString();
    }

    public static bool EndsMidExpression(string code)
    {
        var lexer = new GscLexer();
        LexerResult result;
        try
        {
            result = lexer.Lex(code);
        }
        catch
        {
            return false;
        }

        Token? lastCode = null;
        foreach (var t in result.Tokens)
        {
            if (t.Kind is not TokenKind.Whitespace and not TokenKind.Comment and not TokenKind.EndOfFile)
                lastCode = t;
        }

        if (lastCode is null) return false;

        return lastCode.Value.Kind is
            TokenKind.Plus or TokenKind.Minus or TokenKind.Star or TokenKind.Slash or
            TokenKind.Percent or TokenKind.Ampersand or TokenKind.Pipe or TokenKind.Caret or
            TokenKind.Less or TokenKind.Greater or
            TokenKind.AndAnd or TokenKind.PipePipe or
            TokenKind.EqualsEquals or TokenKind.BangEquals or
            TokenKind.LessEquals or TokenKind.GreaterEquals or
            TokenKind.Equals or
            TokenKind.PlusEquals or TokenKind.MinusEquals or
            TokenKind.StarEquals or TokenKind.SlashEquals or TokenKind.PercentEquals or
            TokenKind.Comma or TokenKind.Question;
    }

    private static bool WantSpaceBetween(Token prev, Token cur, List<Token> tokens, int i, bool curIsTernaryColon)
    {
        var L = prev.Kind;
        var R = cur.Kind;

        if (R == TokenKind.Semicolon) return false;
        if (R == TokenKind.Comma) return false;
        if (R is TokenKind.CloseParen or TokenKind.CloseBracket) return false;
        if (L == TokenKind.OpenBrace && R == TokenKind.CloseBrace) return false;
        if (L == TokenKind.CloseBrace && R == TokenKind.CloseBrace) return false;
        if (R == TokenKind.Dot) return false;
        if (R == TokenKind.Arrow) return false;

        if (R == TokenKind.DoubleColon)
        {
            if (IsValueProducingName(prev)) return false;
            if (L is TokenKind.OpenParen or TokenKind.CloseParen or
                     TokenKind.OpenBracket or TokenKind.CloseBracket)
                return false;
            return true;
        }

        if (R is TokenKind.PlusPlus or TokenKind.MinusMinus)
        {
            if (IsValueProducingName(prev) ||
                L is TokenKind.CloseParen or TokenKind.CloseBracket)
                return false;
        }

        if (R == TokenKind.Colon && !curIsTernaryColon) return false;

        if (L is TokenKind.OpenParen or TokenKind.OpenBracket) return false;
        if (L == TokenKind.Dot) return false;
        if (L == TokenKind.Arrow) return false;
        if (L == TokenKind.DoubleColon) return false;
        if (L is TokenKind.Bang or TokenKind.Tilde) return false;

        if ((L is TokenKind.Minus or TokenKind.Plus or TokenKind.Ampersand) && IsUnary(tokens, i - 1))
            return false;
        if ((L is TokenKind.PlusPlus or TokenKind.MinusMinus) && IsUnary(tokens, i - 1))
            return false;

        if (R == TokenKind.OpenParen)
        {
            if (L == TokenKind.Keyword)
                return KeywordsSpaceBeforeParen.Contains(prev.Text);
            if (L == TokenKind.Identifier)
                return false;
            if (L is TokenKind.CloseParen or TokenKind.CloseBracket)
                return false;
        }

        if (R == TokenKind.OpenBracket)
        {
            if (IsValueProducingName(prev) ||
                L is TokenKind.CloseParen or TokenKind.CloseBracket)
                return false;
        }

        if (L == TokenKind.Directive)
            return R != TokenKind.OpenParen;

        if (L == TokenKind.Slash && R == TokenKind.BadToken && cur.Text == "#")
            return false;
        if (L == TokenKind.BadToken && prev.Text == "#" && R == TokenKind.Slash)
            return false;

        if (L == TokenKind.Ampersand && R == TokenKind.String)
            return false;
        if (L == TokenKind.BadToken && prev.Text == "#" && R == TokenKind.String)
            return false;

        return true;
    }

    private static bool IsValueProducingName(Token token)
    {
        return token.Kind == TokenKind.Identifier ||
               (token.Kind == TokenKind.Keyword && !NonValueKeywords.Contains(token.Text));
    }

    private static bool IsUnary(List<Token> tokens, int idx)
    {
        if (idx == 0) return true;
        var p = tokens[idx - 1];
        return p.Kind switch
        {
            TokenKind.Number => false,
            TokenKind.String => false,
            TokenKind.CloseParen => false,
            TokenKind.CloseBracket => false,
            TokenKind.Identifier => false,
            TokenKind.Keyword => NonValueKeywords.Contains(p.Text),
            _ => true
        };
    }
}
