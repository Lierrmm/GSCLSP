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

                int next;
                bool isBlock;
                if (lineComment >= 0 && (blockComment < 0 || lineComment <= blockComment))
                {
                    next = lineComment;
                    isBlock = false;
                }
                else if (blockComment >= 0)
                {
                    next = blockComment;
                    isBlock = true;
                }
                else
                {
                    ranges.Add((i, line.Length));
                    break;
                }

                if (next > i) ranges.Add((i, next));
                if (!isBlock) break;

                inBlockComment = true;
                i = next + 2;
            }
        }

        return ranges;
    }

    public static bool IsInCode(List<(int Start, int End)> codeRanges, int index)
    {
        foreach (var (start, end) in codeRanges)
            if (index >= start && index < end) return true;
        return false;
    }
}
