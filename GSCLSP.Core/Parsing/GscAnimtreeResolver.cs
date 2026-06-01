using GSCLSP.Core.Models;
using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Parsing;

public readonly record struct AnimtreeUsage(int Line, int Column, int Length);

/// <summary>
/// #animtree refers to the most recent #using_animtree("name") declared above it in the
/// same file. These helpers resolve that "nearest preceding" relationship for hover and
/// diagnostics.
/// </summary>
public static class GscAnimtreeResolver
{
    /// <summary>
    /// Returns the animtree name from the closest #using_animtree above <paramref name="usageLine"/>
    /// (inclusive), or null when none precedes it.
    /// </summary>
    public static string? ResolveActiveAnimtree(string[] lines, int usageLine)
    {
        int last = Math.Min(usageLine, lines.Length - 1);
        for (int i = last; i >= 0; i--)
        {
            var match = UsingAnimtreeRegex().Match(lines[i]);
            if (match.Success) return match.Groups[1].Value;
        }
        return null;
    }

    /// <summary>
    /// Finds every #animtree usage in the file, skipping comments. Used by diagnostics to flag
    /// usages that have no preceding #using_animtree.
    /// </summary>
    public static IEnumerable<AnimtreeUsage> FindUsages(string[] lines)
    {
        bool inBlockComment = false;

        for (int i = 0; i < lines.Length; i++)
        {
            var code = StripComments(lines[i], ref inBlockComment);
            foreach (System.Text.RegularExpressions.Match m in AnimtreeUsageRegex().Matches(code))
                yield return new AnimtreeUsage(i, m.Index, m.Length);
        }
    }

    private static string StripComments(string line, ref bool inBlockComment)
    {
        var chars = line.ToCharArray();
        int i = 0;

        while (i < chars.Length)
        {
            if (inBlockComment)
            {
                int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (close < 0)
                {
                    for (int j = i; j < chars.Length; j++) chars[j] = ' ';
                    break;
                }
                for (int j = i; j < close + 2; j++) chars[j] = ' ';
                inBlockComment = false;
                i = close + 2;
                continue;
            }

            if (i + 1 < chars.Length && chars[i] == '/' && chars[i + 1] == '/')
            {
                for (int j = i; j < chars.Length; j++) chars[j] = ' ';
                break;
            }

            if (i + 1 < chars.Length && chars[i] == '/' && chars[i + 1] == '*')
            {
                inBlockComment = true;
                continue;
            }

            i++;
        }

        return new string(chars);
    }
}
