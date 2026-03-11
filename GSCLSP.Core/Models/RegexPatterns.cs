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
}
