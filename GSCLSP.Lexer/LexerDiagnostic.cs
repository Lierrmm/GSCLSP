namespace GSCLSP.Lexer;

public readonly record struct LexerDiagnostic(
    string Message,
    int Start,
    int Length,
    int Line,
    int Column
);
