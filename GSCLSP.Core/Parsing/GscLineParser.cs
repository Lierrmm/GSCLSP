using GSCLSP.Lexer;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Parsing;

public record ParsedCall(string? Caller, string? Path, string FunctionName, string RawArgs);

public static partial class GscLineParser
{
    public static ParsedCall? ExtractCall(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;

        return ExtractCallWithLexer(line) ?? ExtractCallWithRegex(line);
    }

    private static ParsedCall? ExtractCallWithRegex(string line)
    {
        var match = CallRegex().Match(line);
        if (!match.Success) return null;

        return new ParsedCall(
            Caller: match.Groups[1].Value,
            Path: match.Groups[2].Value,
            FunctionName: match.Groups[3].Value,
            RawArgs: match.Groups[4].Value
        );
    }

    private static ParsedCall? ExtractCallWithLexer(string line)
    {
        var lexer = new GscLexer();
        var tokens = lexer.Lex(line).Tokens
            .Where(t => t.Kind is not TokenKind.Whitespace and not TokenKind.Comment and not TokenKind.Directive and not TokenKind.EndOfFile)
            .ToList();

        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind != TokenKind.OpenParen) continue;
            if (i == 0) continue;

            var functionToken = tokens[i - 1];
            if (!IsNameToken(functionToken)) continue;

            string? path = null;
            string? caller = null;

            if (i >= 3 && tokens[i - 2].Kind == TokenKind.DoubleColon && IsNameToken(tokens[i - 3]))
            {
                path = tokens[i - 3].Text;
                if (i >= 4 && IsNameToken(tokens[i - 4]))
                {
                    caller = tokens[i - 4].Text;
                }
            }
            else if (i >= 2 && IsNameToken(tokens[i - 2]))
            {
                caller = tokens[i - 2].Text;
            }

            return new ParsedCall(
                Caller: caller,
                Path: path,
                FunctionName: functionToken.Text,
                RawArgs: ExtractRawArgs(line, tokens[i].Start)
            );
        }

        return null;
    }

    private static string ExtractRawArgs(string line, int openParenStart)
    {
        if (openParenStart < 0 || openParenStart >= line.Length) return string.Empty;

        int depth = 0;
        int argsStart = openParenStart + 1;
        bool inString = false;
        bool escape = false;

        for (int i = openParenStart; i < line.Length; i++)
        {
            var c = line[i];

            if (inString)
            {
                if (escape)
                {
                    escape = false;
                    continue;
                }

                if (c == '\\')
                {
                    escape = true;
                    continue;
                }

                if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
                continue;
            }

            if (c == '(')
            {
                depth++;
                continue;
            }

            if (c != ')') continue;

            depth--;
            if (depth == 0)
            {
                return line[argsStart..i];
            }
        }

        return openParenStart + 1 < line.Length
            ? line[(openParenStart + 1)..]
            : string.Empty;
    }

    private static bool IsNameToken(Token token)
    {
        return token.Kind is TokenKind.Identifier or TokenKind.Keyword;
    }
}