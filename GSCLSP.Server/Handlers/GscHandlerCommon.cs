namespace GSCLSP.Server.Handlers;

internal static class GscHandlerCommon
{
    public static List<(int Start, int End)> GetCodeRanges(string line, ref bool inBlockComment)
    {
        var ranges = new List<(int Start, int End)>();
        int i = 0;

        while (i < line.Length)
        {
            if (inBlockComment)
            {
                int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (close < 0) return ranges;
                inBlockComment = false;
                i = close + 2;
            }
            else
            {
                int lineComment = line.IndexOf("//", i, StringComparison.Ordinal);
                int blockComment = line.IndexOf("/*", i, StringComparison.Ordinal);
                int stringLiteral = IndexOfStringLiteral(line, i);

                int next = -1;
                int handlerType = 0; // 0=none, 1=lineComment, 2=blockComment, 3=string

                if (lineComment >= 0 && (next < 0 || lineComment < next))
                {
                    next = lineComment;
                    handlerType = 1;
                }
                if (blockComment >= 0 && (next < 0 || blockComment < next))
                {
                    next = blockComment;
                    handlerType = 2;
                }
                if (stringLiteral >= 0 && (next < 0 || stringLiteral < next))
                {
                    next = stringLiteral;
                    handlerType = 3;
                }

                if (handlerType == 0)
                {
                    ranges.Add((i, line.Length));
                    break;
                }

                if (next > i) ranges.Add((i, next));

                if (handlerType == 1) // line comment
                {
                    break;
                }
                else if (handlerType == 2) // block comment
                {
                    inBlockComment = true;
                    i = next + 2;
                }
                else if (handlerType == 3) // string literal
                {
                    // Skip the string content
                    int stringEnd = FindStringEnd(line, next);
                    i = stringEnd;
                }
            }
        }

        return ranges;
    }

    private static int IndexOfStringLiteral(string line, int startIndex)
    {
        for (int i = startIndex; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' || c == '\'')
            {
                // Check if it's escaped
                if (i > 0 && line[i - 1] == '\\')
                {
                    // Count consecutive backslashes
                    int backslashCount = 1;
                    for (int j = i - 2; j >= 0 && line[j] == '\\'; j--)
                        backslashCount++;

                    // If odd number of backslashes, the quote is escaped
                    if (backslashCount % 2 == 1)
                        continue;
                }
                return i;
            }
        }
        return -1;
    }

    private static int FindStringEnd(string line, int stringStart)
    {
        if (stringStart >= line.Length) return line.Length;

        char quoteChar = line[stringStart];
        int i = stringStart + 1;

        while (i < line.Length)
        {
            if (line[i] == quoteChar)
            {
                // Check if it's escaped
                if (i > 0 && line[i - 1] == '\\')
                {
                    // Count consecutive backslashes
                    int backslashCount = 1;
                    for (int j = i - 2; j >= 0 && line[j] == '\\'; j--)
                        backslashCount++;

                    // If odd number of backslashes, the quote is escaped
                    if (backslashCount % 2 == 1)
                    {
                        i++;
                        continue;
                    }
                }
                return i + 1; // Return position after closing quote
            }
            i++;
        }

        return line.Length; // Unclosed string
    }

    public static bool IsInCode(List<(int Start, int End)> codeRanges, int index)
    {
        foreach (var (start, end) in codeRanges)
            if (index >= start && index < end) return true;
        return false;
    }
}
