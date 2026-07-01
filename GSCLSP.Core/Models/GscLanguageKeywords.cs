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
        "isdefined", "istrue",
        "function", "private", "autoexec"
    };

    public static readonly HashSet<string> FunctionModifierKeywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "function", "private", "autoexec"
    };

    /// <summary>
    /// Validates that a prefix consists only of allowed function modifier keywords (function, private, autoexec).
    /// </summary>
    public static bool IsValidFunctionPrefix(ReadOnlySpan<char> prefix)
    {
        if (prefix.IsEmpty)
            return true;

        int i = 0;
        while (i < prefix.Length)
        {
            while (i < prefix.Length && char.IsWhiteSpace(prefix[i])) i++;
            if (i >= prefix.Length)
                return true;

            int start = i;
            while (i < prefix.Length && !char.IsWhiteSpace(prefix[i])) i++;

            var word = prefix[start..i];
            if (!FunctionModifierKeywords.Contains(word.ToString()))
                return false;
        }

        return true;
    }

    public static readonly HashSet<string> TreyarchGscGames = new(StringComparer.OrdinalIgnoreCase)
    {
        "t7", "t8", "t9", "jup"
    };

    public static bool IsTreyarchGscGame(string gameId) => TreyarchGscGames.Contains(gameId);

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
