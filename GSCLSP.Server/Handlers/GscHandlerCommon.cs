using GSCLSP.Core.Indexing;
using GSCLSP.Core.Models;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Server.Handlers;

internal static class GscHandlerCommon
{
    public static bool IsIncludeOrUsingDirective(string trimmedLine) =>
        trimmedLine.StartsWith("#include", StringComparison.Ordinal) ||
        trimmedLine.StartsWith("#using", StringComparison.Ordinal);

    public static bool IsIncludeLikeDirective(string trimmedLine) =>
        IsIncludeOrUsingDirective(trimmedLine) ||
        trimmedLine.StartsWith("#inline", StringComparison.Ordinal);

    public static bool TryExtractDirectivePath(string line, out string path, bool includeInline = true)
    {
        path = string.Empty;
        var trimmed = line.Trim();

        if (IsIncludeOrUsingDirective(trimmed))
        {
            var directiveMatch = DirectivePathRegex().Match(trimmed);
            if (directiveMatch.Success)
            {
                path = directiveMatch.Groups[1].Value.Trim();
                return !string.IsNullOrWhiteSpace(path);
            }
        }

        if (includeInline && trimmed.StartsWith("#inline", StringComparison.Ordinal))
        {
            var inlineMatch = InlinePathRegex().Match(trimmed);
            if (inlineMatch.Success)
            {
                path = inlineMatch.Groups[1].Value.Trim();
                return !string.IsNullOrWhiteSpace(path);
            }
        }

        return false;
    }

    public static bool TryGetLevelFieldAt(string line, int character, out string fieldName)
    {
        fieldName = string.Empty;

        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        int start = Math.Clamp(character, 0, line.Length);
        if (start >= line.Length || !IsWordChar(line[start]))
        {
            if (start > 0 && IsWordChar(line[start - 1])) start--;
            else return false;
        }

        while (start > 0 && IsWordChar(line[start - 1])) start--;

        int end = start;
        while (end < line.Length && IsWordChar(line[end])) end++;

        if (end == start || char.IsDigit(line[start])) return false;

        int i = start - 1;
        while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
        if (i < 0 || line[i] != '.') return false;

        i--;
        while (i >= 0 && char.IsWhiteSpace(line[i])) i--;
        if (i < 0) return false;

        int callerEnd = i + 1;
        int callerStart = callerEnd;
        while (callerStart > 0 && IsWordChar(line[callerStart - 1])) callerStart--;

        if (!line[callerStart..callerEnd].Equals("level", StringComparison.OrdinalIgnoreCase))
            return false;

        // reject chained access like foo.level.x or maps\level.x
        if (callerStart > 0 && (line[callerStart - 1] == '.' || line[callerStart - 1] == '\\'))
            return false;

        fieldName = line[start..end];
        return true;
    }

    public static GscLevelField? ResolveLevelField(GscIndexer indexer, string[] lines, string filePath, string fieldName)
    {
        var docMatches = GscIndexer.ScanLevelFields(lines, filePath)
            .Where(f => f.Name.Equals(fieldName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return docMatches.FirstOrDefault(f => f.Value.Length > 0)
            ?? docMatches.FirstOrDefault()
            ?? indexer.ResolveLevelField(fieldName);
    }

    public static List<(int Start, int End)> GetCodeRanges(string line, ref BlockCommentKind inBlockComment)
    {
        var ranges = new List<(int Start, int End)>();
        int i = 0;

        while (i < line.Length)
        {
            if (inBlockComment != BlockCommentKind.None)
            {
                var closer = inBlockComment == BlockCommentKind.Star ? "*/" : "@/";
                int close = line.IndexOf(closer, i, StringComparison.Ordinal);
                if (close < 0) return ranges;
                inBlockComment = BlockCommentKind.None;
                i = close + 2;
            }
            else
            {
                int lineComment = line.IndexOf("//", i, StringComparison.Ordinal);
                int starComment = line.IndexOf("/*", i, StringComparison.Ordinal);
                int atComment = line.IndexOf("/@", i, StringComparison.Ordinal);
                int stringLiteral = IndexOfStringLiteral(line, i);

                int next = -1;
                int handlerType = 0; // 0=none, 1=lineComment, 2=blockComment, 3=string
                var openedKind = BlockCommentKind.None;

                if (lineComment >= 0 && (next < 0 || lineComment < next))
                {
                    next = lineComment;
                    handlerType = 1;
                }
                if (starComment >= 0 && (next < 0 || starComment < next))
                {
                    next = starComment;
                    handlerType = 2;
                    openedKind = BlockCommentKind.Star;
                }
                if (atComment >= 0 && (next < 0 || atComment < next))
                {
                    next = atComment;
                    handlerType = 2;
                    openedKind = BlockCommentKind.At;
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
                    inBlockComment = openedKind;
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
