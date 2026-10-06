namespace InMemorySqlEngine.Core.Sql;

public enum TokenType
{
    // literals & identifiers
    Identifier,
    Number,
    String,

    // keywords
    Select, From, Where, And, Or, Not, Between, In, Is, Null, Limit, As, True, False,
    Join, On, Group, By, Having, Order, Asc, Desc,
    Create, Index, Using,

    // punctuation
    Comma, Star, LParen, RParen, Dot, Semicolon,

    // operators
    Eq, NotEq, Lt, LtEq, Gt, GtEq,

    Eof
}
