namespace Sift.Core.Sql;

public readonly record struct Token(TokenType Type, string Text, int Position);
