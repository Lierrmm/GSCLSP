using GSCLSP.Core.Models;

namespace GSCLSP.Core.Parsing;

public static class GscFunctionBodyLocator
{
    public static (int BraceStartLine, int BraceEndLine)? FindEnclosingFunctionBodyRange(string[] lines, int cursorLine)
    {
        int funcDefLine = -1;
        for (int i = Math.Min(cursorLine, lines.Length - 1); i >= 0; i--)
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
}
