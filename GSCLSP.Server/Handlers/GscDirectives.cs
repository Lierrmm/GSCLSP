namespace GSCLSP.Server.Handlers;

internal static class GscDirectives
{
    public static readonly string[] All =
    [
        "#include", "#using", "#inline",
        "#define", "#undef",
        "#ifdef", "#ifndef", "#if", "#elif", "#elifdef", "#elifndef",
        "#else", "#endif",
        "#pragma", "#warning", "#error", "#line",
        "#namespace", "#using_animtree"
    ];

    public static readonly string[] MacroIdentifierOperand =
    [
        "#define", "#undef",
        "#ifdef", "#ifndef", "#elifdef", "#elifndef"
    ];

    // this is gsc-tool specific stuff
    public static readonly HashSet<string> BuiltInDefineNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "__FILE__", "__LINE__", "__DATE__", "__TIME__",
        "IW5", "IW6", "IW7", "IW8", "IW9",
        "S1", "S2", "S4",
        "H1", "H2",
        "T6", "T7", "T8", "T9"
    };
}
