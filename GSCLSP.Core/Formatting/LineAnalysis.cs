namespace GSCLSP.Core.Formatting;

internal readonly record struct BraceEvent(bool Open, bool IsSwitch);

internal readonly record struct LineAnalysis(
    string TrimmedEnd,
    string Trimmed,
    bool IsBlank,
    bool IsCommentOnly,
    bool Respaceable,
    int CommentAt,
    bool StartsWithOpenBrace,
    bool IsBracelessHeader,
    bool EndsInBlockComment,
    bool EndsExpectingSwitchBrace,
    int LeadingClosers,
    string FirstWord,
    IReadOnlyList<BraceEvent> Braces,
    int OpenDelta,
    bool HasCodeAfterBlockEnd
);

internal static class LineAnalyzer
{
    private static readonly HashSet<string> ControlKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "for", "while", "foreach", "else", "do"
    };

    public static LineAnalysis Analyze(string line, bool inBlockComment, bool pendingSwitchBrace)
    {
        var n = line.Length;
        var i = 0;

        var inBlock = inBlockComment;
        var inString = false;
        var stringChar = '\0';

        var firstSig = -1;
        var firstSigChar = '\0';
        var firstWord = "";
        var codeStarted = false;
        var sawNonCloser = false;
        var leadingClosers = 0;
        var parenDepth = 0;
        var openDelta = 0;
        var lastCodeChar = '\0';
        var pendingSwitch = pendingSwitchBrace;
        var commentAt = -1;
        var hasInlineBlockComment = false;

        var braces = new List<BraceEvent>();

        while (i < n)
        {
            var c = line[i];
            var c2 = i + 1 < n ? line[i + 1] : '\0';

            if (inBlock)
            {
                if (c == '*' && c2 == '/')
                {
                    inBlock = false;
                    i += 2;
                    continue;
                }
                i++;
                continue;
            }

            if (inString)
            {
                if (c == '\\')
                {
                    i += 2;
                    continue;
                }
                if (c == stringChar)
                {
                    inString = false;
                    lastCodeChar = c;
                }
                i++;
                continue;
            }

            if (c == '/' && c2 == '/')
            {
                if (firstSig < 0) { firstSig = i; firstSigChar = c; }
                commentAt = i;
                break;
            }

            if (c == '/' && c2 == '*')
            {
                if (firstSig < 0) { firstSig = i; firstSigChar = c; }
                hasInlineBlockComment = true;
                inBlock = true;
                i += 2;
                continue;
            }

            if (c == '/' && c2 == '#')
            {
                if (firstSig < 0) { firstSig = i; firstSigChar = c; }
                braces.Add(new BraceEvent(true, false));
                codeStarted = true;
                sawNonCloser = true;
                i += 2;
                continue;
            }

            if (c == '#' && c2 == '/')
            {
                if (firstSig < 0) { firstSig = i; firstSigChar = c; }
                braces.Add(new BraceEvent(false, false));
                if (!sawNonCloser) leadingClosers++;
                codeStarted = true;
                i += 2;
                continue;
            }

            if (c == '"' || c == '\'')
            {
                if (firstSig < 0) { firstSig = i; firstSigChar = c; }
                codeStarted = true;
                sawNonCloser = true;
                inString = true;
                stringChar = c;
                lastCodeChar = c;
                i++;
                continue;
            }

            if (c is ' ' or '\t')
            {
                i++;
                continue;
            }

            if (firstSig < 0) { firstSig = i; firstSigChar = c; }
            lastCodeChar = c;

            if (char.IsLetter(c) || c == '_' || c == '#')
            {
                var j = i;
                while (j < n && (char.IsLetterOrDigit(line[j]) || line[j] == '_' || line[j] == '#'))
                    j++;
                var word = line[i..j];
                if (firstWord.Length == 0 && !sawNonCloser)
                    firstWord = word;
                if (word.Equals("switch", StringComparison.OrdinalIgnoreCase))
                    pendingSwitch = true;
                codeStarted = true;
                sawNonCloser = true;
                i = j;
                continue;
            }

            switch (c)
            {
                case '(':
                    parenDepth++;
                    openDelta++;
                    codeStarted = true;
                    sawNonCloser = true;
                    i++;
                    continue;
                case ')':
                    parenDepth = Math.Max(0, parenDepth - 1);
                    openDelta--;
                    codeStarted = true;
                    sawNonCloser = true;
                    i++;
                    continue;
                case '[':
                    openDelta++;
                    codeStarted = true;
                    sawNonCloser = true;
                    i++;
                    continue;
                case ']':
                    openDelta--;
                    codeStarted = true;
                    sawNonCloser = true;
                    i++;
                    continue;
                case '{':
                    braces.Add(new BraceEvent(true, pendingSwitch));
                    pendingSwitch = false;
                    codeStarted = true;
                    sawNonCloser = true;
                    i++;
                    continue;
                case '}':
                    braces.Add(new BraceEvent(false, false));
                    if (!sawNonCloser) leadingClosers++;
                    codeStarted = true;
                    i++;
                    continue;
            }

            codeStarted = true;
            sawNonCloser = true;
            i++;
        }

        var trimmed = line.Trim();
        var isBlank = firstSig < 0 && !inBlockComment;
        var hasCodeAfterBlock = inBlockComment && codeStarted;
        var isCommentOnly = !codeStarted && !isBlank && !inBlockComment;
        var hasOpenBrace = braces.Exists(b => b.Open);
        var isBracelessHeader =
            codeStarted &&
            !hasOpenBrace &&
            parenDepth == 0 &&
            lastCodeChar != ';' &&
            ControlKeywords.Contains(firstWord);

        return new LineAnalysis(
            TrimmedEnd: line.TrimEnd(),
            Trimmed: trimmed,
            IsBlank: isBlank,
            IsCommentOnly: isCommentOnly,
            Respaceable: codeStarted && !hasInlineBlockComment,
            CommentAt: commentAt,
            StartsWithOpenBrace: firstSigChar == '{',
            IsBracelessHeader: isBracelessHeader,
            EndsInBlockComment: inBlock,
            EndsExpectingSwitchBrace: pendingSwitch,
            LeadingClosers: leadingClosers,
            FirstWord: firstWord,
            Braces: braces,
            OpenDelta: openDelta,
            HasCodeAfterBlockEnd: hasCodeAfterBlock
        );
    }
}
