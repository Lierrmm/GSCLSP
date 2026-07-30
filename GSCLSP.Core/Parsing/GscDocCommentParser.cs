using System.Text;
using System.Text.RegularExpressions;

namespace GSCLSP.Core.Parsing;

public static partial class GscDocCommentParser
{
    private const string BlockStart = "/@";
    private const string BlockEnd = "@/";
    private const string DescriptionTag = "DESCRIPTION";
    private const string UsageTag = "USAGE";

    [GeneratedRegex(@"^\[(?<tag>[A-Z][A-Z0-9_]*)\]\s*:\s*(?<rest>.*)$")]
    private static partial Regex TagRegex();

    [GeneratedRegex(@"<[^<>\s]+>|\[[^\[\]\s]+\]")]
    private static partial Regex PlaceholderRegex();

    public static bool EndsDocBlock(string line) => line.TrimEnd().EndsWith(BlockEnd, StringComparison.Ordinal);

    public static string? TryRenderBlockEndingAt(IReadOnlyList<string> lines, int endLineIndex)
    {
        if (endLineIndex < 0 || endLineIndex >= lines.Count || !EndsDocBlock(lines[endLineIndex]))
            return null;

        for (int start = endLineIndex; start >= 0; start--)
        {
            if (start != endLineIndex && EndsDocBlock(lines[start]))
                return null;

            if (!lines[start].TrimStart().StartsWith(BlockStart, StringComparison.Ordinal))
                continue;

            var block = new List<string>(endLineIndex - start + 1);
            for (int i = start; i <= endLineIndex; i++)
                block.Add(lines[i]);

            var rendered = Render(block);
            return string.IsNullOrEmpty(rendered) ? null : rendered;
        }

        return null;
    }

    public static string Render(IEnumerable<string> rawLines)
    {
        var entries = Parse(rawLines);
        if (entries.Count == 0)
            return string.Empty;

        var sections = new List<string>();

        foreach (var (tag, value) in entries)
        {
            if (tag == DescriptionTag)
                sections.Add(string.Join("  \n", value.Select(EscapePlaceholders)));
        }

        foreach (var (tag, value) in entries)
        {
            if (tag == UsageTag)
                sections.Add($"**Usage:**\n```gsc\n{string.Join("\n", value)}\n```");
        }

        foreach (var (tag, value) in entries)
        {
            if (tag is DescriptionTag or UsageTag)
                continue;

            sections.Add($"**{TitleFor(tag)}:** {string.Join(" ", value.Select(EscapePlaceholders))}");
        }

        return string.Join("\n\n", sections);
    }

    private static List<(string Tag, List<string> Value)> Parse(IEnumerable<string> rawLines)
    {
        var entries = new List<(string, List<string>)>();

        string? currentTag = null;
        List<string>? currentValue = null;

        void Flush()
        {
            if (currentTag == null || currentValue == null)
                return;

            if (currentValue.Count > 0)
                entries.Add((currentTag, currentValue));

            currentTag = null;
            currentValue = null;
        }

        foreach (var rawLine in rawLines)
        {
            var line = rawLine.Trim();

            if (line.StartsWith(BlockStart, StringComparison.Ordinal))
                line = line[BlockStart.Length..].Trim();

            if (line.EndsWith(BlockEnd, StringComparison.Ordinal))
            {
                line = line[..^BlockEnd.Length].Trim();
                if (line.Length == 0)
                {
                    Flush();
                    break;
                }
            }

            var match = TagRegex().Match(line);
            if (match.Success)
            {
                Flush();
                currentTag = match.Groups["tag"].Value;
                currentValue = [];
                line = match.Groups["rest"].Value.Trim();
            }
            else if (currentValue == null)
            {
                continue;
            }

            bool terminated = line.EndsWith(';');
            if (terminated)
                line = line[..^1].TrimEnd();

            if (line.Length > 0)
                currentValue!.Add(line);

            if (terminated)
                Flush();
        }

        Flush();
        return entries;
    }

    private static string EscapePlaceholders(string value) =>
        PlaceholderRegex().Replace(value, m => $"`{m.Value}`");

    private static string TitleFor(string tag)
    {
        var sb = new StringBuilder(tag.Length);
        foreach (var c in tag)
            sb.Append(c == '_' ? ' ' : char.ToLowerInvariant(c));

        sb[0] = char.ToUpperInvariant(sb[0]);
        return sb.ToString();
    }
}
