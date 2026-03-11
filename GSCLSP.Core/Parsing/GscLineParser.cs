using static GSCLSP.Core.Models.RegexPatterns;

namespace GSCLSP.Core.Parsing;

public record ParsedCall(string? Caller, string? Path, string FunctionName, string RawArgs);

public static partial class GscLineParser
{
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