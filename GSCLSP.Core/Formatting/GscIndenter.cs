namespace GSCLSP.Core.Formatting;

internal sealed class IndentState
{
    public List<IndentFrame> Stack { get; } = [];
    public bool InBlock { get; set; }
    public bool PendingSwitchBrace { get; set; }
    public int ParenCarry { get; set; }
    public bool PrevContinuesExpr { get; set; }
}

internal sealed class IndentFrame
{
    public bool IsSwitch { get; init; }
    public bool CaseOpen { get; set; }
    public bool Virtual { get; init; }
}

internal static class GscIndenter
{
    public static List<string> Indent(List<string> lines, string indentUnit, IndentState? state = null)
    {
        state ??= new IndentState();
        var result = new List<string>(lines.Count);
        foreach (var line in lines)
            result.Add(IndentLine(state, line, indentUnit));
        return result;
    }

    private static bool IsPreprocessorDirective(string trimmed)
    {
        if (trimmed.Length == 0 || trimmed[0] != '#') return false;
        return MatchDirective(trimmed, "#ifdef")
            || MatchDirective(trimmed, "#ifndef")
            || MatchDirective(trimmed, "#elifdef")
            || MatchDirective(trimmed, "#elifndef")
            || MatchDirective(trimmed, "#else")
            || MatchDirective(trimmed, "#endif")
            || MatchDirective(trimmed, "#define");
    }

    private static bool MatchDirective(string trimmed, string directive)
    {
        if (!trimmed.StartsWith(directive, StringComparison.OrdinalIgnoreCase)) return false;
        if (trimmed.Length == directive.Length) return true;
        var next = trimmed[directive.Length];
        return !char.IsLetterOrDigit(next) && next != '_';
    }

    public static string IndentLine(IndentState state, string line, string indentUnit)
    {
        var info = LineAnalyzer.Analyze(line, state.InBlock, state.PendingSwitchBrace);
        var startedInBlock = state.InBlock;
        state.InBlock = info.EndsInBlockComment;
        state.PendingSwitchBrace = info.EndsExpectingSwitchBrace;

        if (info.IsBlank) return "";
        if (startedInBlock && !info.HasCodeAfterBlockEnd) return info.TrimmedEnd;
        if (IsPreprocessorDirective(info.Trimmed)) return info.Trimmed;

        var stack = state.Stack;
        if (info.StartsWithOpenBrace)
        {
            if (stack.Count > 0 && stack[^1].Virtual)
                stack.RemoveAt(stack.Count - 1);
        }

        var isLabel = info.FirstWord.Equals("case", StringComparison.OrdinalIgnoreCase) ||
                      info.FirstWord.Equals("default", StringComparison.OrdinalIgnoreCase);
        var continuation = (state.ParenCarry > 0 || state.PrevContinuesExpr) ? 1 : 0;
        var indentLevel = IndentForLine(stack, info.LeadingClosers, isLabel) + continuation;
        var output = indentLevel > 0 ? string.Concat(Enumerable.Repeat(indentUnit, indentLevel)) + info.Trimmed : info.Trimmed;
        state.ParenCarry = Math.Max(0, state.ParenCarry + info.OpenDelta);

        if (!info.IsCommentOnly)
            state.PrevContinuesExpr = GscRespacer.EndsMidExpression(info.Trimmed);

        ApplyBraces(stack, info);

        if (stack.Count > 0 && isLabel && stack[^1].IsSwitch)
            stack[^1].CaseOpen = true;

        if (info.IsCommentOnly)
        {
            // Comments don't open or resolve braceless bodies.
        }
        else if (info.IsBracelessHeader)
        {
            stack.Add(new IndentFrame { IsSwitch = false, CaseOpen = false, Virtual = true });
        }
        else if (!info.StartsWithOpenBrace && state.ParenCarry == 0 && !state.PrevContinuesExpr)
        {
            while (stack.Count > 0 && stack[^1].Virtual)
                stack.RemoveAt(stack.Count - 1);
        }

        return output;
    }

    private static void ApplyBraces(List<IndentFrame> stack, LineAnalysis info)
    {
        foreach (var ev in info.Braces)
        {
            if (ev.Open)
            {
                stack.Add(new IndentFrame { IsSwitch = ev.IsSwitch, CaseOpen = false, Virtual = false });
            }
            else
            {
                while (stack.Count > 0 && stack[^1].Virtual)
                    stack.RemoveAt(stack.Count - 1);
                if (stack.Count > 0)
                    stack.RemoveAt(stack.Count - 1);
            }
        }
    }

    private static int CountCaseOpen(List<IndentFrame> frames)
    {
        var count = 0;
        foreach (var f in frames)
        {
            if (f.IsSwitch && f.CaseOpen)
                count++;
        }
        return count;
    }

    private static int IndentForLine(List<IndentFrame> stack, int leadingClosers, bool isLabel)
    {
        if (leadingClosers > 0)
        {
            var realToClose = leadingClosers;
            var idx = stack.Count;
            while (idx > 0 && realToClose > 0)
            {
                idx--;
                if (!stack[idx].Virtual)
                    realToClose--;
            }
            var remaining = stack.GetRange(0, idx);
            return remaining.Count + CountCaseOpen(remaining);
        }

        var top = stack.Count > 0 ? stack[^1] : null;
        var indent = stack.Count + CountCaseOpen(stack);
        if (isLabel && top is { IsSwitch: true, CaseOpen: true })
            indent -= 1;
        return Math.Max(0, indent);
    }
}
