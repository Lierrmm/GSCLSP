using System.Text.RegularExpressions;

namespace GSCLSP.Core.Parsing;

public record ParsedCall(string? Caller, string? Path, string FunctionName, string RawArgs);

public static partial class GscLineParser
{
    // Updated Regex to capture the caller (like 'player' or 'self')
    // It looks for: (caller)? (path::)? function(args)
    [GeneratedRegex(@"(?:(\w+)\s+)?(?:([\w\\]+)::)?(\w+)\s*\((.*)\)")]
    private static partial Regex CallRegex();

    public static ParsedCall? ExtractCall(string line)
    {
        var match = CallRegex().Match(line);
        if (!match.Success) return null;

        return new ParsedCall(
            Caller: match.Groups[1].Value, // "player" or "self"
            Path: match.Groups[2].Value,
            FunctionName: match.Groups[3].Value,
            RawArgs: match.Groups[4].Value
        );
    }
}