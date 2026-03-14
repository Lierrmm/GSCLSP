namespace GSCLSP.Core.Models;

public static class GscLanguageKeywords
{
    public static readonly HashSet<string> DiagnosticReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "foreach", "while", "switch", "return", "wait",
        "waittill", "waittillmatch", "waittillframeend", "notify", "endon",
        "thread", "childthread", "break", "continue", "case", "default"
    };

    public static readonly HashSet<string> LocalVariableReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "foreach", "while", "switch", "return", "wait",
        "waittill", "waittillmatch", "waittillframeend", "notify", "endon",
        "thread", "childthread", "break", "continue", "case", "default",
        "true", "false", "undefined", "self", "level", "game"
    };
}
