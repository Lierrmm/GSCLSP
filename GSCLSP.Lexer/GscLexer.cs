namespace GSCLSP.Lexer;

public sealed class GscLexer
{
    private static readonly HashSet<string> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        "if", "else", "for", "foreach", "while", "switch", "return", "wait",
        "waittill", "waittillmatch", "waittillframeend", "notify", "endon",
        "thread", "childthread", "break", "continue", "case", "default",
        "true", "false", "undefined", "self", "level", "game"
    };

    private string _source = string.Empty;
    private int _position;
    private int _line;
    private int _column;
    private readonly List<Token> _tokens = [];
    private readonly List<LexerDiagnostic> _diagnostics = [];

    public LexerResult Lex(string source)
    {
        _source = source ?? string.Empty;
        _position = 0;
        _line = 0;
        _column = 0;
        _tokens.Clear();
        _diagnostics.Clear();
        _tokens.EnsureCapacity((_source.Length / 3) + 1);

        while (!IsAtEnd)
        {
            var start = _position;
            var line = _line;
            var column = _column;
            var current = Current;

            if (char.IsWhiteSpace(current))
            {
                ReadWhitespace();
                AddToken(TokenKind.Whitespace, start, line, column);
                continue;
            }

            if (current == '#' && IsStartOfDirective())
            {
                ReadDirective();
                AddToken(TokenKind.Directive, start, line, column);
                continue;
            }

            if (current == '/' && Peek(1) == '/')
            {
                ReadSingleLineComment();
                AddToken(TokenKind.Comment, start, line, column);
                continue;
            }

            if (current == '/' && Peek(1) == '*')
            {
                ReadBlockComment();
                AddToken(TokenKind.Comment, start, line, column);
                continue;
            }

            if (current == '"')
            {
                ReadString(start, line, column);
                continue;
            }

            if (IsIdentifierStart(current))
            {
                ReadIdentifier();
                var text = Slice(start);
                var kind = Keywords.Contains(text) ? TokenKind.Keyword : TokenKind.Identifier;
                AddToken(kind, text, start, line, column);
                continue;
            }

            if (char.IsDigit(current))
            {
                ReadNumber();
                AddToken(TokenKind.Number, start, line, column);
                continue;
            }

            if (TryReadOperatorOrPunctuation(start, line, column))
            {
                continue;
            }

            Advance();
            AddToken(TokenKind.BadToken, start, line, column);
            _diagnostics.Add(new LexerDiagnostic($"Unexpected character '{current}'.", start, 1, line, column));
        }

        _tokens.Add(new Token(TokenKind.EndOfFile, string.Empty, _position, 0, _line, _column));

        return new LexerResult
        {
            Tokens = [.. _tokens],
            Diagnostics = [.. _diagnostics]
        };
    }

    private bool IsAtEnd => _position >= _source.Length;

    private char Current => IsAtEnd ? '\0' : _source[_position];

    private char Peek(int offset)
    {
        var index = _position + offset;
        return index >= _source.Length ? '\0' : _source[index];
    }

    private bool IsStartOfDirective()
    {
        if (_position == 0)
        {
            return true;
        }

        var previous = _source[_position - 1];
        return previous == '\n' || previous == '\r';
    }

    private void ReadWhitespace()
    {
        while (!IsAtEnd && char.IsWhiteSpace(Current))
        {
            Advance();
        }
    }

    private void ReadDirective()
    {
        while (!IsAtEnd && Current != '\n' && Current != '\r')
        {
            Advance();
        }
    }

    private void ReadSingleLineComment()
    {
        Advance();
        Advance();

        while (!IsAtEnd && Current != '\n' && Current != '\r')
        {
            Advance();
        }
    }

    private void ReadBlockComment()
    {
        var start = _position;
        var line = _line;
        var column = _column;

        Advance();
        Advance();

        while (!IsAtEnd)
        {
            if (Current == '*' && Peek(1) == '/')
            {
                Advance();
                Advance();
                return;
            }

            Advance();
        }

        _diagnostics.Add(new LexerDiagnostic("Unterminated block comment.", start, _position - start, line, column));
    }

    private void ReadString(int start, int line, int column)
    {
        Advance();

        while (!IsAtEnd)
        {
            if (Current == '\\')
            {
                Advance();
                if (!IsAtEnd)
                {
                    Advance();
                }

                continue;
            }

            if (Current == '"')
            {
                Advance();
                AddToken(TokenKind.String, start, line, column);
                return;
            }

            if (Current == '\n' || Current == '\r')
            {
                break;
            }

            Advance();
        }

        AddToken(TokenKind.BadToken, start, line, column);
        _diagnostics.Add(new LexerDiagnostic("Unterminated string literal.", start, _position - start, line, column));
    }

    private void ReadIdentifier()
    {
        while (!IsAtEnd && IsIdentifierPart(Current))
        {
            Advance();
        }
    }

    private void ReadNumber()
    {
        if (Current == '0' && (Peek(1) == 'x' || Peek(1) == 'X'))
        {
            Advance();
            Advance();

            while (!IsAtEnd && IsHexDigit(Current))
            {
                Advance();
            }

            return;
        }

        while (!IsAtEnd && char.IsDigit(Current))
        {
            Advance();
        }

        if (!IsAtEnd && Current == '.' && char.IsDigit(Peek(1)))
        {
            Advance();

            while (!IsAtEnd && char.IsDigit(Current))
            {
                Advance();
            }
        }
    }

    private bool TryReadOperatorOrPunctuation(int start, int line, int column)
    {
        var twoChar = Current switch
        {
            '+' when Peek(1) == '+' => TokenKind.PlusPlus,
            '-' when Peek(1) == '-' => TokenKind.MinusMinus,
            '+' when Peek(1) == '=' => TokenKind.PlusEquals,
            '-' when Peek(1) == '=' => TokenKind.MinusEquals,
            '*' when Peek(1) == '=' => TokenKind.StarEquals,
            '/' when Peek(1) == '=' => TokenKind.SlashEquals,
            '%' when Peek(1) == '=' => TokenKind.PercentEquals,
            '=' when Peek(1) == '=' => TokenKind.EqualsEquals,
            '!' when Peek(1) == '=' => TokenKind.BangEquals,
            '<' when Peek(1) == '=' => TokenKind.LessEquals,
            '>' when Peek(1) == '=' => TokenKind.GreaterEquals,
            '&' when Peek(1) == '&' => TokenKind.AndAnd,
            '|' when Peek(1) == '|' => TokenKind.PipePipe,
            '-' when Peek(1) == '>' => TokenKind.Arrow,
            ':' when Peek(1) == ':' => TokenKind.DoubleColon,
            _ => (TokenKind)(-1)
        };

        if ((int)twoChar != -1)
        {
            Advance();
            Advance();
            AddToken(twoChar, start, line, column);
            return true;
        }

        var oneChar = Current switch
        {
            '(' => TokenKind.OpenParen,
            ')' => TokenKind.CloseParen,
            '{' => TokenKind.OpenBrace,
            '}' => TokenKind.CloseBrace,
            '[' => TokenKind.OpenBracket,
            ']' => TokenKind.CloseBracket,
            ',' => TokenKind.Comma,
            '.' => TokenKind.Dot,
            ':' => TokenKind.Colon,
            ';' => TokenKind.Semicolon,
            '?' => TokenKind.Question,
            '+' => TokenKind.Plus,
            '-' => TokenKind.Minus,
            '*' => TokenKind.Star,
            '/' => TokenKind.Slash,
            '%' => TokenKind.Percent,
            '&' => TokenKind.Ampersand,
            '|' => TokenKind.Pipe,
            '^' => TokenKind.Caret,
            '!' => TokenKind.Bang,
            '~' => TokenKind.Tilde,
            '=' => TokenKind.Equals,
            '<' => TokenKind.Less,
            '>' => TokenKind.Greater,
            _ => (TokenKind)(-1)
        };

        if ((int)oneChar == -1)
        {
            return false;
        }

        Advance();
        AddToken(oneChar, start, line, column);
        return true;
    }

    private void Advance()
    {
        if (IsAtEnd)
        {
            return;
        }

        var c = _source[_position++];

        if (c == '\r')
        {
            if (!IsAtEnd && _source[_position] == '\n')
            {
                _position++;
            }

            _line++;
            _column = 0;
            return;
        }

        if (c == '\n')
        {
            _line++;
            _column = 0;
            return;
        }

        _column++;
    }

    private void AddToken(TokenKind kind, int start, int line, int column)
    {
        var text = Slice(start);
        _tokens.Add(new Token(kind, text, start, _position - start, line, column));
    }

    private void AddToken(TokenKind kind, string text, int start, int line, int column)
    {
        _tokens.Add(new Token(kind, text, start, _position - start, line, column));
    }

    private string Slice(int start)
    {
        var length = _position - start;
        if (length <= 0)
        {
            return string.Empty;
        }

        return _source.Substring(start, length);
    }

    private static bool IsIdentifierStart(char c) => c == '_' || char.IsLetter(c);

    private static bool IsIdentifierPart(char c) => c == '_' || c == '\\' || char.IsLetterOrDigit(c);

    private static bool IsHexDigit(char c) =>
        (c >= '0' && c <= '9') ||
        (c >= 'a' && c <= 'f') ||
        (c >= 'A' && c <= 'F');
}
