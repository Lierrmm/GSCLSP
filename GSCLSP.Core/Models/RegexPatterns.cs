using System.Text.RegularExpressions;

namespace GSCLSP.Core.Models;

public partial class RegexPatterns
{

    [GeneratedRegex(@"^#include\s+([\w\\]+)(;?)")]
    public static partial Regex IncludeRegex();

    [GeneratedRegex(@"([\w\\]+)::$")]
    public static partial Regex NameSpaceRegex();

    [GeneratedRegex(@"^(?<name>[a-zA-Z_]\w*)\s*\((?<params>[^)]*)\)", RegexOptions.Multiline)]
    public static partial Regex FunctionMultiLineRegex();

    [GeneratedRegex(@"(Summary|Example|MandatoryArg|OptionalArg|Module|CallOn|SPMP):")]
    public static partial Regex DocRegex();

    [GeneratedRegex(@"^#(?:include|using)\s+([\w\\]+)")]
    public static partial Regex DirectivePathRegex();

    [GeneratedRegex(@"^#inline\s+([\w\\]+(?:\.\w+)?)")]
    public static partial Regex InlinePathRegex();

    [GeneratedRegex(@"([\w\\]*\\[\w\\]+)::")]
    public static partial Regex NamespacePathRegex();

    [GeneratedRegex(@"//.*")]
    public static partial Regex CommentRegex();

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    public static partial Regex MultilineCommentRegex();

    [GeneratedRegex(@"\s+")]
    public static partial Regex WhiteSpaceTabRegex();

    [GeneratedRegex(@"(?:(\w+)\s+)?(?:([\w\\]+)::)?(\w+)\s*\((.*)\)")]
    public static partial Regex CallRegex();

    [GeneratedRegex(@"^\s+([a-zA-Z_]\w*)\s*=\s*(.+?)\s*;")]
    public static partial Regex LocalVarAssignmentRegex();

    [GeneratedRegex(@"^([A-Za-z_]\w*)\s*=\s*(.+);(?:\s*//\s*(.*))?\s*$")]
    public static partial Regex GlobalVarAssignmentRegex();

    [GeneratedRegex(@"(?:(?<path>[a-zA-Z_]\w*(?:\\[a-zA-Z_]\w*)*)::|(?<global>::))?(?<name>[a-zA-Z_]\w*)\s*\(")]
    public static partial Regex CallSiteRegex();

    [GeneratedRegex(@"\b(?<name>[A-Za-z_]\w*)\s*\(")]
    public static partial Regex CallFuncRegex();

    [GeneratedRegex(@"\b(?<name>(?:[A-Za-z_]\w*|0[xX][0-9A-Fa-f]+|\d+))\s*\(")]
    public static partial Regex BuiltinCallRegex();

    [GeneratedRegex(@"\{\s*0x(?<id>[0-9A-Fa-f]+)\s*,\s*""(?<name>[^""]+)""\s*\}", RegexOptions.Compiled)]
    public static partial Regex GscToolRegex();
}
