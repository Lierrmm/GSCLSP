using GSCLSP.Lexer;

namespace GSCLSP.Server.Handlers;

internal static class GscLexingHelper
{
    public static LexerResult Lex(string content)
    {
        var lexer = new GscLexer();
        return lexer.Lex(content);
    }

    public static Token? GetTokenAtOrBeforePosition(IReadOnlyList<Token> tokens, int line, int character)
    {
        var atPosition = GetTokenAtPosition(tokens, line, character);
        if (atPosition is not null)
        {
            return atPosition;
        }

        if (character > 0)
        {
            return GetTokenAtPosition(tokens, line, character - 1);
        }

        return null;
    }

    public static bool IsInsideFunctionArgumentList(IReadOnlyList<Token> tokens, int line, int character)
    {
        var openParens = new Stack<bool>();

        foreach (var token in tokens)
        {
            if (token.Kind == TokenKind.EndOfFile)
                break;

            if (token.Line > line || (token.Line == line && token.Column >= character))
                break;

            if (token.Kind == TokenKind.OpenParen)
            {
                openParens.Push(IsCallOpenParen(tokens, token));
                continue;
            }

            if (token.Kind == TokenKind.CloseParen && openParens.Count > 0)
            {
                openParens.Pop();
            }
        }

        return openParens.Any(v => v);
    }

    private static Token? GetTokenAtPosition(IReadOnlyList<Token> tokens, int line, int character)
    {
        foreach (var token in tokens)
        {
            if (token.Length == 0 || token.Line != line)
                continue;

            if (character >= token.Column && character < token.Column + token.Length)
                return token;
        }

        return null;
    }

    private static bool IsCallOpenParen(IReadOnlyList<Token> tokens, Token openParen)
    {
        var previous = default(Token?);

        foreach (var token in tokens)
        {
            if (token.Start >= openParen.Start)
                break;

            if (token.Kind is TokenKind.Whitespace or TokenKind.Comment)
                continue;

            previous = token;
        }

        if (previous is null)
            return false;

        var p = previous.Value;
        return p.Kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.CloseBracket or TokenKind.CloseParen;
    }
}
