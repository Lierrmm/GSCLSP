using System.Text.RegularExpressions;

namespace GSCLSP.Core.Models;

public partial class RegexPatterns
{
    [GeneratedRegex(@"^([a-zA-Z_]\w*)\s*\(([^)]*)\)", RegexOptions.Compiled)]
    public static partial Regex FunctionLineRegex();

    [GeneratedRegex(@"^#include\s+([\w\\]+)(;?)", RegexOptions.Compiled)]
    public static partial Regex IncludeRegex();

    [GeneratedRegex(@"([\w\\]+)::$", RegexOptions.Compiled)]
    public static partial Regex NameSpaceRegex();

    [GeneratedRegex(@"^(?<name>[a-zA-Z_][a-zA-Z0-0_]*)\s*\(", RegexOptions.Multiline | RegexOptions.Compiled)]
    public static partial Regex FunctionDefinitionRegex();

    [GeneratedRegex(@"(Summary|Example|MandatoryArg|OptionalArg|Module|CallOn|SPMP):", RegexOptions.Compiled)]
    public static partial Regex DocRegex();

    // #include #using paths
    [GeneratedRegex(@"^#(?:include|using)\s+([\w\\]+)", RegexOptions.Compiled)]
    public static partial Regex DirectivePathRegex();

    [GeneratedRegex(@"^#using_animtree\(\s?""\w+""\s?\);", RegexOptions.Compiled)]
    public static partial Regex UsingAnimRegex();

    // #inline .gsh
    [GeneratedRegex(@"^#inline\s+([\w\\]+(?:\.\w+)?)", RegexOptions.Compiled)]
    public static partial Regex InlinePathRegex();

    //// path::func
    [GeneratedRegex(@"([\w\\]*\\[\w\\]+)::", RegexOptions.Compiled)]
    public static partial Regex NamespacePathRegex();
}
