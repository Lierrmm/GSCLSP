using System.Text.RegularExpressions;

namespace GSCLSP.Core.Formatting;

internal static class GscBraceRewriter
{
    public static List<string> ToAllman(List<string> lines, bool seedInBlock = false)
    {
        var res = new List<string>(lines.Count);
        var inBlock = seedInBlock;

        foreach (var line in lines)
        {
            var s = SplitCodeComment(line, inBlock);
            inBlock = s.EndsInBlock;
            if (!s.Transformable) { res.Add(line); continue; }

            var cur = s.Code;
            var pieces = new List<string>();

            if (Regex.IsMatch(cur, @"^\}\s*else\b", RegexOptions.IgnoreCase))
            {
                pieces.Add(s.Indent + "}");
                cur = Regex.Replace(cur, @"^\}\s*", "");
            }

            if (cur.Length > 1 && cur.EndsWith('{'))
            {
                var header = cur[..^1].TrimEnd();
                if (header.Length > 0)
                {
                    pieces.Add(s.Indent + header);
                    pieces.Add(s.Indent + "{" + (s.Comment.Length > 0 ? " " + s.Comment : ""));
                }
                else
                {
                    pieces.Add(s.Indent + cur + (s.Comment.Length > 0 ? " " + s.Comment : ""));
                }
            }
            else
            {
                pieces.Add(s.Indent + cur + (s.Comment.Length > 0 ? " " + s.Comment : ""));
            }

            var original = s.Indent + s.Code + (s.Comment.Length > 0 ? " " + s.Comment : "");
            if (pieces.Count == 1 && pieces[0] == original)
            {
                res.Add(line);
            }
            else
            {
                res.AddRange(pieces);
            }
        }

        return res;
    }

    private readonly record struct CodeCommentSplit(
        string Indent,
        string Code,
        string Comment,
        bool EndsInBlock,
        bool Transformable
    );

    private static CodeCommentSplit SplitCodeComment(string line, bool inBlock)
    {
        var n = line.Length;
        var i = 0;
        var blk = inBlock;
        var str = false;
        var sc = '\0';
        var commentAt = -1;
        var hasInlineBlock = false;

        while (i < n)
        {
            var c = line[i];
            var c2 = i + 1 < n ? line[i + 1] : '\0';

            if (blk)
            {
                if (c == '*' && c2 == '/') { blk = false; i += 2; continue; }
                i++;
                continue;
            }

            if (str)
            {
                if (c == '\\') { i += 2; continue; }
                if (c == sc) str = false;
                i++;
                continue;
            }

            if (c == '/' && c2 == '/') { commentAt = i; break; }
            if (c == '/' && c2 == '*') { hasInlineBlock = true; blk = true; i += 2; continue; }
            if (c == '"' || c == '\'') { str = true; sc = c; i++; continue; }
            i++;
        }

        var indentMatch = Regex.Match(line, @"^\s*");
        var indent = indentMatch.Value;
        var codeRaw = commentAt >= 0 ? line[..commentAt] : line;
        var code = codeRaw.Trim();
        var comment = commentAt >= 0 ? line[commentAt..] : "";
        var transformable = !inBlock && !hasInlineBlock && code.Length > 0;
        return new CodeCommentSplit(indent, code, comment, blk, transformable);
    }
}
