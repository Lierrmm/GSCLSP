using GSCLSP.Lexer;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Diagnostics;

public static class GscDeadCodeAnalyzer
{
    public readonly record struct EarlyReturn(int Line, int Column, int Length);

    public sealed record Result(List<InactiveRange> DeadRanges, List<EarlyReturn> EarlyReturns);

    public static Result Analyze(string[] lines, IReadOnlyList<Token> tokens)
    {
        var deadRanges = new List<InactiveRange>();
        var earlyReturns = new List<EarlyReturn>();

        var sig = tokens.Where(IsSignificant).ToList();

        foreach (var functionBodyOpenBraceIndex in FindFunctionBodyOpenBraces(lines, sig))
        {
            int idx = functionBodyOpenBraceIndex;
            ParseBlock(sig, ref idx, isFunctionBody: true, deadRanges, earlyReturns);
        }

        return new Result(deadRanges, earlyReturns);
    }

    private static void ParseBlock(
        List<Token> sig,
        ref int idx,
        bool isFunctionBody,
        List<InactiveRange> deadRanges,
        List<EarlyReturn> earlyReturns)
    {
        int openBraceLine = sig[idx].Line;
        idx++;

        while (idx < sig.Count && sig[idx].Kind != TokenKind.CloseBrace)
        {
            var t = sig[idx];
            bool isTopLevelReturn = isFunctionBody && IsReturnKeyword(t);
            var returnLine = t.Line;
            var returnCol = t.Column;
            var returnLen = t.Length;

            ParseStatement(sig, ref idx, deadRanges, earlyReturns);

            if (isTopLevelReturn)
            {
                // check whether anything significant remains before the closing brace
                if (idx < sig.Count && sig[idx].Kind != TokenKind.CloseBrace)
                {
                    var closeBraceIdx = FindMatchingCloseBraceFrom(sig, idx);
                    if (closeBraceIdx < 0) return;

                    var closeBraceLine = sig[closeBraceIdx].Line;
                    
                    // line after the return's semicolon through the line before the closing brace.
                    var semicolonLine = FindPrevSemicolonLine(sig, idx - 1, stopAfter: returnLine);
                    var deadStart = Math.Max(returnLine + 1, semicolonLine + 1);
                    var deadEnd = closeBraceLine - 1;
                    if (deadEnd >= deadStart)
                        deadRanges.Add(new InactiveRange(deadStart, deadEnd));

                    earlyReturns.Add(new EarlyReturn(returnLine, returnCol, returnLen));

                    idx = closeBraceIdx;
                }
                return;
            }
        }

        if (idx < sig.Count && sig[idx].Kind == TokenKind.CloseBrace) idx++;
    }

    private static void ParseStatement(
        List<Token> sig,
        ref int idx,
        List<InactiveRange> deadRanges,
        List<EarlyReturn> earlyReturns)
    {
        if (idx >= sig.Count) return;
        var t = sig[idx];

        if (t.Kind == TokenKind.OpenBrace)
        {
            ParseBlock(sig, ref idx, isFunctionBody: false, deadRanges, earlyReturns);
            return;
        }

        if (t.Kind == TokenKind.Semicolon) { idx++; return; }

        if (IsKeyword(t, "if") || IsKeyword(t, "while") || IsKeyword(t, "for") || IsKeyword(t, "foreach"))
        {
            idx++;
            SkipParenGroup(sig, ref idx);
            ParseStatement(sig, ref idx, deadRanges, earlyReturns);
            if (IsKeyword(t, "if") && idx < sig.Count && IsKeyword(sig[idx], "else"))
            {
                idx++;
                ParseStatement(sig, ref idx, deadRanges, earlyReturns);
            }
            return;
        }

        if (IsKeyword(t, "do"))
        {
            idx++;
            ParseStatement(sig, ref idx, deadRanges, earlyReturns);
            if (idx < sig.Count && IsKeyword(sig[idx], "while"))
            {
                idx++;
                SkipParenGroup(sig, ref idx);
                SkipToSemicolon(sig, ref idx);
            }
            return;
        }

        if (IsKeyword(t, "switch"))
        {
            idx++;
            SkipParenGroup(sig, ref idx);
            if (idx < sig.Count && sig[idx].Kind == TokenKind.OpenBrace)
                ParseBlock(sig, ref idx, isFunctionBody: false, deadRanges, earlyReturns);
            return;
        }

        SkipToSemicolon(sig, ref idx);
    }

    private static void SkipParenGroup(List<Token> sig, ref int idx)
    {
        if (idx >= sig.Count || sig[idx].Kind != TokenKind.OpenParen) return;
        int depth = 0;
        while (idx < sig.Count)
        {
            var k = sig[idx].Kind;
            if (k == TokenKind.OpenParen) depth++;
            else if (k == TokenKind.CloseParen)
            {
                depth--;
                if (depth == 0) { idx++; return; }
            }
            idx++;
        }
    }

    private static void SkipToSemicolon(List<Token> sig, ref int idx)
    {
        int parenDepth = 0;
        int bracketDepth = 0;
        while (idx < sig.Count)
        {
            var k = sig[idx].Kind;
            if (k == TokenKind.OpenParen) parenDepth++;
            else if (k == TokenKind.CloseParen) parenDepth = Math.Max(0, parenDepth - 1);
            else if (k == TokenKind.OpenBracket) bracketDepth++;
            else if (k == TokenKind.CloseBracket) bracketDepth = Math.Max(0, bracketDepth - 1);
            else if (k == TokenKind.Semicolon && parenDepth == 0 && bracketDepth == 0)
            {
                idx++;
                return;
            }
            else if (k == TokenKind.CloseBrace && parenDepth == 0 && bracketDepth == 0)
            {
                // missing semicolon so let the outer block handle the `}`
                return;
            }
            idx++;
        }
    }

    private static int FindMatchingCloseBraceFrom(List<Token> sig, int startIdx)
    {
        int depth = 1;
        for (int i = startIdx; i < sig.Count; i++)
        {
            if (sig[i].Kind == TokenKind.OpenBrace) depth++;
            else if (sig[i].Kind == TokenKind.CloseBrace)
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
    }

    private static int FindPrevSemicolonLine(List<Token> sig, int startIdx, int stopAfter)
    {
        for (int i = startIdx; i >= 0 && sig[i].Line >= stopAfter; i--)
        {
            if (sig[i].Kind == TokenKind.Semicolon) return sig[i].Line;
        }
        return stopAfter;
    }

    private static IEnumerable<int> FindFunctionBodyOpenBraces(string[] lines, List<Token> sig)
    {
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            var line = lines[lineIndex];
            if (line.Length == 0 || char.IsWhiteSpace(line[0])) continue;
            if (line.TrimEnd().EndsWith(';')) continue;
            if (!FunctionMultiLineRegex().Match(line).Success) continue;

            for (int i = 0; i < sig.Count; i++)
            {
                if (sig[i].Line < lineIndex) continue;
                if (sig[i].Kind == TokenKind.OpenBrace)
                {
                    yield return i;
                    break;
                }
            }
        }
    }

    private static bool IsReturnKeyword(Token t) =>
        (t.Kind is TokenKind.Identifier or TokenKind.Keyword) && t.Text.Equals("return", StringComparison.Ordinal);

    private static bool IsKeyword(Token t, string text) =>
        (t.Kind is TokenKind.Identifier or TokenKind.Keyword) && t.Text.Equals(text, StringComparison.Ordinal);

    private static bool IsSignificant(Token t) =>
        t.Kind is not TokenKind.Whitespace
            and not TokenKind.Comment
            and not TokenKind.EndOfFile
            and not TokenKind.BadToken;
}
