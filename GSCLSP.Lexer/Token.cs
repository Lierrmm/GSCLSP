namespace GSCLSP.Lexer;

public readonly record struct Token(
    TokenKind Kind,
    string Text,
    int Start,
    int Length,
    int Line,
    int Column
);
