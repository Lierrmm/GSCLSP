namespace GSCLSP.Core.Formatting;

public static class GscFormatter
{
    public static string Format(string source, bool insertSpaces = false, int tabSize = 4)
    {
        if (source.Length == 0) return source;

        var eol = source.Contains("\r\n") ? "\r\n" : "\n";
        var indentUnit = insertSpaces ? new string(' ', tabSize) : "\t";
        var rawLines = source.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        var lines = new List<string>(rawLines);
        lines = GscRespacer.Respace(lines);
        lines = GscBraceRewriter.ToAllman(lines);
        lines = GscIndenter.Indent(lines, indentUnit);
        lines = GscFormatterCleanup.Cleanup(lines);

        return string.Join(eol, lines) + eol;
    }

    public static string FormatRange(
        string source,
        int startLine,
        int endLine,
        bool insertSpaces = false,
        int tabSize = 4)
    {
        var eol = source.Contains("\r\n") ? "\r\n" : "\n";
        var indentUnit = insertSpaces ? new string(' ', tabSize) : "\t";
        var allLines = source.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);

        startLine = Math.Max(0, startLine);
        endLine = Math.Min(allLines.Length - 1, endLine);
        if (startLine > endLine) return "";

        // Seed indent state by running the indenter over preceding lines.
        var state = new IndentState();
        for (var i = 0; i < startLine; i++)
            GscIndenter.IndentLine(state, allLines[i], indentUnit);
        var seedInBlock = state.InBlock;

        var selection = new List<string>(allLines[startLine..(endLine + 1)]);
        selection = GscRespacer.Respace(selection, seedInBlock);
        selection = GscBraceRewriter.ToAllman(selection, seedInBlock);
        selection = GscIndenter.Indent(selection, indentUnit, state);
        selection = GscFormatterCleanup.Cleanup(selection);

        return string.Join(eol, selection);
    }
}
