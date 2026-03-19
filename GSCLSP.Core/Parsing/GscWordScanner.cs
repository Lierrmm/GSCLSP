using GSCLSP.Lexer;

namespace GSCLSP.Core.Parsing;

public static class GscWordScanner
{
    // Characters that can be part of a GSC function call or path
    private static readonly char[] GscIdentifierChars =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_\\:".ToCharArray();

    public static string GetFullIdentifierAt(string line, int characterIndex)
    {
        if (string.IsNullOrEmpty(line)) return "";

        // Adjust index if cursor is at the end of the line
        int index = characterIndex;
        if (index >= line.Length && index > 0) index--;
        if (index < 0 || index >= line.Length) return "";

        // Use lexer to avoid scanning inside comments/strings/directives and to validate token context.
        var lexer = new GscLexer();
        var tokenResult = lexer.Lex(line);
        var tokenIndex = FindTokenIndex(tokenResult.Tokens, index);
        if (tokenIndex < 0) return "";

        var token = tokenResult.Tokens[tokenIndex];
        if (!IsIdentifierContextToken(token))
        {
            // try one char left, matching existing cursor behavior
            if (index > 0)
            {
                tokenIndex = FindTokenIndex(tokenResult.Tokens, index - 1);
                if (tokenIndex < 0 || !IsIdentifierContextToken(tokenResult.Tokens[tokenIndex]))
                {
                    return "";
                }

                index--;
            }
            else
            {
                return "";
            }
        }

        // Preserve previous contiguous identifier semantics for paths like maps\mp\foo::bar.
        if (!GscIdentifierChars.Contains(line[index]))
        {
            if (index > 0 && GscIdentifierChars.Contains(line[index - 1]))
                index--;
            else
                return "";
        }

        int start = index;
        int end = index;

        // Expand Left
        while (start > 0 && GscIdentifierChars.Contains(line[start - 1]))
        {
            start--;
        }

        // Expand Right
        while (end < line.Length && GscIdentifierChars.Contains(line[end]))
        {
            end++;
        }

        return line[start..end].Trim();
    }

    private static int FindTokenIndex(IReadOnlyList<Token> tokens, int index)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            if (token.Length == 0) continue;

            if (index >= token.Start && index < token.Start + token.Length)
            {
                return i;
            }
        }

        return -1;
    }

    private static bool IsIdentifierContextToken(Token token)
    {
        return token.Kind is TokenKind.Identifier or TokenKind.Keyword or TokenKind.DoubleColon;
    }
}