namespace Sift.Core.Sql;

/// <summary>Deliberately boring: a straight-line hand-rolled tokenizer, no regex, no generated code.</summary>
public sealed class Lexer
{
    private static readonly Dictionary<string, TokenType> Keywords = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SELECT"] = TokenType.Select,
        ["FROM"] = TokenType.From,
        ["WHERE"] = TokenType.Where,
        ["AND"] = TokenType.And,
        ["OR"] = TokenType.Or,
        ["NOT"] = TokenType.Not,
        ["BETWEEN"] = TokenType.Between,
        ["IN"] = TokenType.In,
        ["IS"] = TokenType.Is,
        ["NULL"] = TokenType.Null,
        ["LIMIT"] = TokenType.Limit,
        ["AS"] = TokenType.As,
        ["TRUE"] = TokenType.True,
        ["FALSE"] = TokenType.False,
        ["JOIN"] = TokenType.Join,
        ["ON"] = TokenType.On,
        ["GROUP"] = TokenType.Group,
        ["BY"] = TokenType.By,
        ["HAVING"] = TokenType.Having,
        ["ORDER"] = TokenType.Order,
        ["ASC"] = TokenType.Asc,
        ["DESC"] = TokenType.Desc,
        ["CREATE"] = TokenType.Create,
        ["INDEX"] = TokenType.Index,
        ["USING"] = TokenType.Using,
    };

    private readonly string _text;
    private int _pos;

    public Lexer(string text) => _text = text;

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        Token token;
        do
        {
            token = NextToken();
            tokens.Add(token);
        } while (token.Type != TokenType.Eof);
        return tokens;
    }

    private char Current => _pos < _text.Length ? _text[_pos] : '\0';
    private char Peek(int ahead = 1) => _pos + ahead < _text.Length ? _text[_pos + ahead] : '\0';

    private Token NextToken()
    {
        SkipWhitespace();
        if (_pos >= _text.Length) return new Token(TokenType.Eof, "", _pos);

        var start = _pos;
        var ch = Current;

        if (char.IsLetter(ch) || ch == '_') return ReadIdentifierOrKeyword(start);
        if (char.IsDigit(ch)) return ReadNumber(start);
        // '-' is only ever a literal's sign here, never subtraction — there's no arithmetic in
        // this grammar (docs/design.md §1), so a '-' immediately before a digit is unambiguous.
        if (ch == '-' && char.IsDigit(Peek())) return ReadNumber(start);
        if (ch == '\'') return ReadString(start);

        switch (ch)
        {
            case ',': _pos++; return new Token(TokenType.Comma, ",", start);
            case '*': _pos++; return new Token(TokenType.Star, "*", start);
            case '(': _pos++; return new Token(TokenType.LParen, "(", start);
            case ')': _pos++; return new Token(TokenType.RParen, ")", start);
            case '.': _pos++; return new Token(TokenType.Dot, ".", start);
            case ';': _pos++; return new Token(TokenType.Semicolon, ";", start);
            case '=': _pos++; return new Token(TokenType.Eq, "=", start);
            case '!':
                if (Peek() == '=') { _pos += 2; return new Token(TokenType.NotEq, "!=", start); }
                throw Error($"unexpected character '{ch}'", start);
            case '<':
                if (Peek() == '=') { _pos += 2; return new Token(TokenType.LtEq, "<=", start); }
                if (Peek() == '>') { _pos += 2; return new Token(TokenType.NotEq, "<>", start); }
                _pos++; return new Token(TokenType.Lt, "<", start);
            case '>':
                if (Peek() == '=') { _pos += 2; return new Token(TokenType.GtEq, ">=", start); }
                _pos++; return new Token(TokenType.Gt, ">", start);
            default:
                throw Error($"unexpected character '{ch}'", start);
        }
    }

    private void SkipWhitespace()
    {
        while (_pos < _text.Length && char.IsWhiteSpace(_text[_pos])) _pos++;
    }

    private Token ReadIdentifierOrKeyword(int start)
    {
        while (char.IsLetterOrDigit(Current) || Current == '_') _pos++;
        var text = _text[start.._pos];
        var type = Keywords.GetValueOrDefault(text, TokenType.Identifier);
        return new Token(type, text, start);
    }

    private Token ReadNumber(int start)
    {
        if (Current == '-') _pos++;
        while (char.IsDigit(Current)) _pos++;
        if (Current == '.' && char.IsDigit(Peek()))
        {
            _pos++;
            while (char.IsDigit(Current)) _pos++;
        }
        return new Token(TokenType.Number, _text[start.._pos], start);
    }

    private Token ReadString(int start)
    {
        _pos++; // opening quote
        var value = new System.Text.StringBuilder();
        while (true)
        {
            if (_pos >= _text.Length) throw Error("unterminated string literal", start);
            if (Current == '\'')
            {
                if (Peek() == '\'') { value.Append('\''); _pos += 2; continue; } // escaped ''
                _pos++;
                break;
            }
            value.Append(Current);
            _pos++;
        }
        return new Token(TokenType.String, value.ToString(), start);
    }

    private static SqlParseException Error(string message, int position) => new(message, position);
}

public sealed class SqlParseException : Exception
{
    public int Position { get; }
    public SqlParseException(string message, int position) : base($"{message} (at position {position})")
    {
        Position = position;
    }
}
