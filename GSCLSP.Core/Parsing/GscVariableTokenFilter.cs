using GSCLSP.Lexer;

namespace GSCLSP.Core.Parsing;

public static class GscVariableTokenFilter
{
    public static bool IsNamespaceOrMemberUsage(IReadOnlyList<Token> tokens, int index)
    {
        if (FindSignificantToken(tokens, index, 1)?.Kind is TokenKind.DoubleColon)
            return true;

        return FindSignificantToken(tokens, index, -1)?.Kind is TokenKind.DoubleColon or TokenKind.Dot;
    }

    public static Token? FindSignificantToken(IReadOnlyList<Token> tokens, int index, int step)
    {
        ArgumentOutOfRangeException.ThrowIfZero(step);

        for (int i = index + step; i >= 0 && i < tokens.Count; i += step)
        {
            if (tokens[i].Kind is TokenKind.Whitespace or TokenKind.Comment)
                continue;

            return tokens[i];
        }

        return null;
    }
}
