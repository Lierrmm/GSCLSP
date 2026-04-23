namespace GSCLSP.Core.Diagnostics;

public readonly record struct InactiveRange(int StartLine, int EndLine);

public static class GscInactiveRegionAnalyzer
{
    public static List<InactiveRange> Analyze(string[] lines, string currentGame)
    {
        var result = new List<InactiveRange>();
        var stack = new Stack<Frame>();

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();
            if (trimmed.Length == 0 || trimmed[0] != '#') continue;

            if (TryMatchDirective(trimmed, "#ifdef", out var name))
            {
                PushBranch(stack, matches: NameMatches(name, currentGame), lineIndex: i);
                continue;
            }

            if (TryMatchDirective(trimmed, "#ifndef", out name))
            {
                PushBranch(stack, matches: !NameMatches(name, currentGame), lineIndex: i);
                continue;
            }

            if (TryMatchDirective(trimmed, "#elifdef", out name))
            {
                SwitchBranch(stack, result, matches: NameMatches(name, currentGame), lineIndex: i);
                continue;
            }

            if (TryMatchDirective(trimmed, "#elifndef", out name))
            {
                SwitchBranch(stack, result, matches: !NameMatches(name, currentGame), lineIndex: i);
                continue;
            }

            if (IsBareDirective(trimmed, "#else"))
            {
                SwitchBranch(stack, result, matches: true, lineIndex: i);
                continue;
            }

            if (IsBareDirective(trimmed, "#endif"))
            {
                if (stack.Count == 0) continue;
                var top = stack.Pop();
                FlushIfInactive(result, top, endLine: i - 1);
            }
        }

        return result;
    }

    private static void PushBranch(Stack<Frame> stack, bool matches, int lineIndex)
    {
        var parentInactive = stack.Count > 0 && stack.Peek().IsEffectivelyInactive();
        var branchActive = !parentInactive && matches;
        stack.Push(new Frame(parentInactive, BranchActive: branchActive, AnyBranchTaken: branchActive, BranchStartLine: lineIndex));
    }

    private static void SwitchBranch(Stack<Frame> stack, List<InactiveRange> result, bool matches, int lineIndex)
    {
        if (stack.Count == 0) return;

        var top = stack.Pop();
        FlushIfInactive(result, top, endLine: lineIndex - 1);

        var branchActive = !top.ParentInactive && !top.AnyBranchTaken && matches;
        stack.Push(top with
        {
            BranchActive = branchActive,
            AnyBranchTaken = top.AnyBranchTaken || branchActive,
            BranchStartLine = lineIndex,
        });
    }

    private static void FlushIfInactive(List<InactiveRange> result, Frame frame, int endLine)
    {
        if (frame.ParentInactive || frame.BranchActive) return;
        if (endLine < frame.BranchStartLine + 1) return;
        result.Add(new InactiveRange(frame.BranchStartLine + 1, endLine));
    }

    private static bool NameMatches(string directiveName, string currentGame) =>
        directiveName.Equals(currentGame, StringComparison.OrdinalIgnoreCase);

    private static bool TryMatchDirective(string trimmedLine, string directive, out string argument)
    {
        argument = string.Empty;
        if (!trimmedLine.StartsWith(directive, StringComparison.Ordinal)) return false;
        if (trimmedLine.Length == directive.Length) return false;
        if (!char.IsWhiteSpace(trimmedLine[directive.Length])) return false;

        var rest = trimmedLine[directive.Length..].Trim();
        int end = 0;
        while (end < rest.Length && (char.IsLetterOrDigit(rest[end]) || rest[end] == '_'))
            end++;

        if (end == 0) return false;
        argument = rest[..end];
        return true;
    }

    private static bool IsBareDirective(string trimmedLine, string directive)
    {
        if (!trimmedLine.StartsWith(directive, StringComparison.Ordinal)) return false;
        if (trimmedLine.Length == directive.Length) return true;
        var next = trimmedLine[directive.Length];
        return char.IsWhiteSpace(next) || next == '/';
    }

    private sealed record Frame(bool ParentInactive, bool BranchActive, bool AnyBranchTaken, int BranchStartLine)
    {
        public bool IsEffectivelyInactive() => ParentInactive || !BranchActive;
    }
}
