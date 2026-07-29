using System.Globalization;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Sql;

/// <summary>
/// Recursive-descent parser for the in-scope grammar (see PLAN.md §4): a single SELECT with
/// FROM / WHERE / LIMIT. Deliberately minimal — see PLAN.md §2 on why parsing isn't the star here.
/// </summary>
public sealed class Parser
{
    private readonly List<Token> _tokens;
    private int _pos;

    private Parser(List<Token> tokens) => _tokens = tokens;

    public static SelectStatement ParseSelect(string sql)
    {
        var tokens = new Lexer(sql).Tokenize();
        return new Parser(tokens).ParseSelectStatement();
    }

    private Token Current => _tokens[_pos];

    private Token Advance() => _tokens[_pos++];

    private bool Match(TokenType type)
    {
        if (Current.Type != type) return false;
        _pos++;
        return true;
    }

    private Token Expect(TokenType type)
    {
        if (Current.Type != type)
            throw Error($"expected {type} but found {Current.Type} ('{Current.Text}')");
        return Advance();
    }

    private SqlParseException Error(string message) => new(message, Current.Position);

    // select_stmt := SELECT select_list FROM table_ref (WHERE expr)? (LIMIT NUMBER)? SEMICOLON? EOF
    private SelectStatement ParseSelectStatement()
    {
        Expect(TokenType.Select);
        var columns = ParseSelectList();
        Expect(TokenType.From);
        var from = ParseTableRef();

        Expr? where = null;
        if (Match(TokenType.Where)) where = ParseOr();

        int? limit = null;
        if (Match(TokenType.Limit)) limit = int.Parse(Expect(TokenType.Number).Text, CultureInfo.InvariantCulture);

        Match(TokenType.Semicolon);
        Expect(TokenType.Eof);

        return new SelectStatement(columns, from, where, limit);
    }

    private List<SelectItem> ParseSelectList()
    {
        var items = new List<SelectItem>();
        if (Match(TokenType.Star))
        {
            items.Add(new SelectItem(IsStar: true, ColumnName: null, Alias: null));
            return items;
        }

        items.Add(ParseSelectItem());
        while (Match(TokenType.Comma)) items.Add(ParseSelectItem());
        return items;
    }

    private SelectItem ParseSelectItem()
    {
        var name = Expect(TokenType.Identifier).Text;
        string? alias = null;
        if (Match(TokenType.As)) alias = Expect(TokenType.Identifier).Text;
        return new SelectItem(IsStar: false, ColumnName: name, Alias: alias);
    }

    private TableRef ParseTableRef()
    {
        var name = Expect(TokenType.Identifier).Text;
        string? alias = null;
        if (Match(TokenType.As)) alias = Expect(TokenType.Identifier).Text;
        else if (Current.Type == TokenType.Identifier) alias = Advance().Text;
        return new TableRef(name, alias);
    }

    // or_expr := and_expr (OR and_expr)*
    private Expr ParseOr()
    {
        var left = ParseAnd();
        while (Match(TokenType.Or)) left = new LogicalExpr(left, LogicalOp.Or, ParseAnd());
        return left;
    }

    // and_expr := not_expr (AND not_expr)*
    private Expr ParseAnd()
    {
        var left = ParseNot();
        while (Match(TokenType.And)) left = new LogicalExpr(left, LogicalOp.And, ParseNot());
        return left;
    }

    // not_expr := NOT not_expr | LPAREN or_expr RPAREN | predicate
    private Expr ParseNot()
    {
        if (Match(TokenType.Not)) return new NotExpr(ParseNot());

        if (Match(TokenType.LParen))
        {
            var inner = ParseOr();
            Expect(TokenType.RParen);
            return inner;
        }

        return ParsePredicate();
    }

    // predicate := operand ( comparison_op operand
    //                      | BETWEEN operand AND operand
    //                      | IN LPAREN operand (COMMA operand)* RPAREN
    //                      | IS NOT? NULL )?
    private Expr ParsePredicate()
    {
        var left = ParseOperand();

        if (Match(TokenType.Between))
        {
            var low = ParseOperand();
            Expect(TokenType.And);
            var high = ParseOperand();
            return new BetweenExpr(left, low, high);
        }

        if (Match(TokenType.In))
        {
            Expect(TokenType.LParen);
            var items = new List<Expr> { ParseOperand() };
            while (Match(TokenType.Comma)) items.Add(ParseOperand());
            Expect(TokenType.RParen);
            return new InExpr(left, items);
        }

        if (Match(TokenType.Is))
        {
            var negated = Match(TokenType.Not);
            Expect(TokenType.Null);
            return new IsNullExpr(left, negated);
        }

        var op = TryParseComparisonOp();
        if (op is { } comparisonOp) return new ComparisonExpr(left, comparisonOp, ParseOperand());

        return left; // bare BOOL-typed operand, e.g. `WHERE is_active`
    }

    private ComparisonOp? TryParseComparisonOp()
    {
        var op = Current.Type switch
        {
            TokenType.Eq => ComparisonOp.Eq,
            TokenType.NotEq => ComparisonOp.NotEq,
            TokenType.Lt => ComparisonOp.Lt,
            TokenType.LtEq => ComparisonOp.LtEq,
            TokenType.Gt => ComparisonOp.Gt,
            TokenType.GtEq => ComparisonOp.GtEq,
            _ => (ComparisonOp?)null
        };
        if (op is not null) Advance();
        return op;
    }

    // operand := NUMBER | STRING | TRUE | FALSE | column_ref
    private Expr ParseOperand()
    {
        var token = Current;
        switch (token.Type)
        {
            case TokenType.Number:
                Advance();
                return new LiteralExpr(token.Text.Contains('.')
                    ? SqlValue.Decimal(decimal.Parse(token.Text, CultureInfo.InvariantCulture))
                    : SqlValue.Int(long.Parse(token.Text, CultureInfo.InvariantCulture)));
            case TokenType.String:
                Advance();
                return new LiteralExpr(SqlValue.Text(token.Text));
            case TokenType.True:
                Advance();
                return new LiteralExpr(SqlValue.Bool(true));
            case TokenType.False:
                Advance();
                return new LiteralExpr(SqlValue.Bool(false));
            case TokenType.Identifier:
                return ParseColumnRef();
            default:
                throw Error($"expected literal or column reference but found {token.Type} ('{token.Text}')");
        }
    }

    // column_ref := IDENTIFIER (DOT IDENTIFIER)?
    private ColumnRefExpr ParseColumnRef()
    {
        var first = Expect(TokenType.Identifier).Text;
        if (Match(TokenType.Dot))
        {
            var second = Expect(TokenType.Identifier).Text;
            return new ColumnRefExpr(first, second);
        }
        return new ColumnRefExpr(null, first);
    }
}
