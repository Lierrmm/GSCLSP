namespace GSCLSP.Core.Models;

public static class GscLanguageKeywords
{
    public static readonly HashSet<string> DiagnosticReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "do", "while", "for", "foreach", "in",
        "switch", "case", "default", "break", "continue", "return",
        "wait", "waitframe", "waittill", "waittillmatch", "waittillframeend",
        "notify", "endon",
        "thread", "childthread", "thisthread", "call",
        "breakpoint", "prof_begin", "prof_end",
        "assert", "assertex", "assertmsg",
        "true", "false", "undefined",
        "size", "game", "self", "anim", "level",
        "isdefined", "istrue"
    };

    public static readonly HashSet<string> LocalVariableReservedWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "do", "while", "for", "foreach", "in",
        "switch", "case", "default", "break", "continue", "return",
        "wait", "waitframe", "waittill", "waittillmatch", "waittillframeend",
        "notify", "endon",
        "thread", "childthread", "thisthread", "call",
        "true", "false", "undefined",
        "self", "level", "game", "anim"
    };
}
