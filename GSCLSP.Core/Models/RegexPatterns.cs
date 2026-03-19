using System.Text.RegularExpressions;

namespace GSCLSP.Core.Models;

public partial class RegexPatterns
{

    [GeneratedRegex(@"^#include\s+([\w\\]+)(;?)", RegexOptions.Compiled)]
    public static partial Regex IncludeRegex();

    [GeneratedRegex(@"([\w\\]+)::$", RegexOptions.Compiled)]
    public static partial Regex NameSpaceRegex();

    [GeneratedRegex(@"^(?<name>[a-zA-Z_]\w*)\s*\((?<params>[^)]*)\)", RegexOptions.Multiline | RegexOptions.Compiled)]
    public static partial Regex FunctionMultiLineRegex();

    [GeneratedRegex(@"(Summary|Example|MandatoryArg|OptionalArg|Module|CallOn|SPMP):", RegexOptions.Compiled)]
    public static partial Regex DocRegex();

    [GeneratedRegex(@"^#(?:include|using)\s+([\w\\]+)", RegexOptions.Compiled)]
    public static partial Regex DirectivePathRegex();

    [GeneratedRegex(@"^#inline\s+([\w\\]+(?:\.\w+)?)", RegexOptions.Compiled)]
    public static partial Regex InlinePathRegex();

    [GeneratedRegex(@"([\w\\]*\\[\w\\]+)::", RegexOptions.Compiled)]
    public static partial Regex NamespacePathRegex();

    [GeneratedRegex(@"//.*", RegexOptions.Compiled)]
    public static partial Regex CommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.Compiled)]
    public static partial Regex MultilineCommentRegex();

    [GeneratedRegex(@"\s+", RegexOptions.Compiled)]
    public static partial Regex WhiteSpaceTabRegex();

    [GeneratedRegex(@"(?:(\w+)\s+)?(?:([\w\\]+)::)?(\w+)\s*\((.*)\)", RegexOptions.Compiled)]
    public static partial Regex CallRegex();

    [GeneratedRegex(@"^\s+([a-zA-Z_]\w*)\s*=\s*(.+?)\s*;", RegexOptions.Compiled)]
    public static partial Regex LocalVarAssignmentRegex();

    [GeneratedRegex(@"(?:(?<path>[a-zA-Z_]\w*(?:\\[a-zA-Z_]\w*)*)::|(?<global>::))?(?<name>[a-zA-Z_]\w*)\s*\(", RegexOptions.Compiled)]
    public static partial Regex CallSiteRegex();

    [GeneratedRegex(@"(?<lhs>[a-zA-Z_][\w.]*)\s*=\s*(?:(?<path>[a-zA-Z_]\w*(?:\\[a-zA-Z_]\w*)*)::|(?<global>::))(?<name>[a-zA-Z_]\w*)\s*;", RegexOptions.Compiled)]
    public static partial Regex FunctionPointerAssignmentRegex();

    [GeneratedRegex(@"\[\[\s*(?<target>[^\]]+?)\s*\]\]\s*\(", RegexOptions.Compiled)]
    public static partial Regex FunctionPointerCallRegex();

    [GeneratedRegex(@"gsclsp-ignore\s*(?::|=)?\s*semicolon\b", RegexOptions.CultureInvariant)]
    public static partial Regex MuteSemicolonRegex();

    [GeneratedRegex(@"gsclsp-ignore\s*(?::|=)?\s*recursive\b", RegexOptions.CultureInvariant)]
    public static partial Regex MuteRecursiveRegex();

    [GeneratedRegex(@"gsclsp-ignore\s*(?::|=)?\s*unresolved\b", RegexOptions.CultureInvariant)]
    public static partial Regex MuteUnresolvedRegex();

    [GeneratedRegex(@"gsclsp-ignore\s*(?::|=)?\s*unused\b", RegexOptions.CultureInvariant)]
    public static partial Regex MuteUnusedRegex();
}
