namespace GSCLSP.Lexer;

public sealed class LexerResult
{
    public required IReadOnlyList<Token> Tokens { get; init; }
    public required IReadOnlyList<LexerDiagnostic> Diagnostics { get; init; }
}
