namespace GSCLSP.Core.Formatting;

internal static class GscFormatterCleanup
{
    public static List<string> Cleanup(List<string> lines)
    {
        var result = new List<string>(lines.Count);
        var blanks = 0;

        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.Length == 0)
            {
                var prevKept = result.Count > 0 ? result[^1] : null;
                var afterOpen = prevKept is not null && CodeEndsWith(prevKept, '{');

                var j = i + 1;
                while (j < lines.Count && lines[j].Length == 0) j++;
                var next = j < lines.Count ? lines[j] : null;
                var beforeClose = next is not null && next.TrimStart().StartsWith('}');

                if (afterOpen || beforeClose) continue;

                if (++blanks <= 1)
                    result.Add("");
            }
            else
            {
                blanks = 0;
                result.Add(l);
            }
        }

        while (result.Count > 0 && result[0].Length == 0)
            result.RemoveAt(0);
        while (result.Count > 0 && result[^1].Length == 0)
            result.RemoveAt(result.Count - 1);

        return result;
    }

    private static bool CodeEndsWith(string line, char c)
    {
        var n = line.Length;
        var lastCode = -1;
        var inStr = false;
        var sc = '\0';
        var inBlk = false;
        var i = 0;

        while (i < n)
        {
            var ch = line[i];
            var ch2 = i + 1 < n ? line[i + 1] : '\0';

            if (inBlk)
            {
                if (ch == '*' && ch2 == '/') { inBlk = false; i += 2; continue; }
                i++; continue;
            }
            if (inStr)
            {
                if (ch == '\\') { i += 2; continue; }
                if (ch == sc) inStr = false;
                i++; continue;
            }
            if (ch == '/' && ch2 == '/') break;
            if (ch == '/' && ch2 == '*') { inBlk = true; i += 2; continue; }
            if (ch == '"' || ch == '\'') { inStr = true; sc = ch; i++; continue; }
            if (ch is not ' ' and not '\t') lastCode = i;
            i++;
        }

        return lastCode >= 0 && line[lastCode] == c;
    }
}
