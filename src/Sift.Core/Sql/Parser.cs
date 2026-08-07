using System.Globalization;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Sql;

/// <summary>
/// Recursive-descent parser for the in-scope grammar (see PLAN.md §4): a single SELECT with
/// JOIN / WHERE / GROUP BY / HAVING / ORDER BY / LIMIT. Deliberately minimal — see PLAN.md §2
/// on why parsing isn't the star here.
/// </summary>
public sealed class Parser
{
    private static readonly HashSet<string> AggregateFunctionNames =
        new(StringComparer.OrdinalIgnoreCase) { "COUNT", "SUM", "AVG", "MIN", "MAX" };

    private readonly List<Token> _tokens;
    private int _pos;

    private Parser(List<Token> tokens) => _tokens = tokens;

    public static SelectStatement ParseSelect(string sql)
    {
        var tokens = new Lexer(sql).Tokenize();
        return new Parser(tokens).ParseSelectStatement();
    }

    /// <summary>Dispatches on the leading keyword: `SELECT ...` or `CREATE INDEX ...`.</summary>
    public static Statement Parse(string sql)
    {
        var tokens = new Lexer(sql).Tokenize();
        var parser = new Parser(tokens);
        return parser.Current.Type switch
        {
            TokenType.Select => parser.ParseSelectStatement(),
            TokenType.Create => parser.ParseCreateIndexStatement(),
            _ => throw parser.Error($"expected SELECT or CREATE but found {parser.Current.Type} ('{parser.Current.Text}')")
        };
    }

    private Token Current => _tokens[_pos];

    private Token PeekToken(int ahead = 1) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];

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

    // select_stmt := SELECT select_list FROM table_ref (JOIN table_ref ON col = col)*
    //                (WHERE expr)? (GROUP BY col_list)? (HAVING expr)?
    //                (ORDER BY order_item_list)? (LIMIT NUMBER)? SEMICOLON? EOF
    private SelectStatement ParseSelectStatement()
    {
        Expect(TokenType.Select);
        var columns = ParseSelectList();
        Expect(TokenType.From);
        var from = ParseTableRef();

        var joins = new List<JoinClause>();
        while (Match(TokenType.Join))
        {
            var table = ParseTableRef();
            Expect(TokenType.On);
            var left = ParseColumnRef();
            Expect(TokenType.Eq);
            var right = ParseColumnRef();
            joins.Add(new JoinClause(table, left, right));
        }

        Expr? where = null;
        if (Match(TokenType.Where)) where = ParseOr();

        var groupBy = new List<ColumnRefExpr>();
        if (Match(TokenType.Group))
        {
            Expect(TokenType.By);
            groupBy.Add(ParseColumnRef());
            while (Match(TokenType.Comma)) groupBy.Add(ParseColumnRef());
        }

        Expr? having = null;
        if (Match(TokenType.Having)) having = ParseOr();

        var orderBy = new List<OrderByItem>();
        if (Match(TokenType.Order))
        {
            Expect(TokenType.By);
            orderBy.Add(ParseOrderByItem());
            while (Match(TokenType.Comma)) orderBy.Add(ParseOrderByItem());
        }

        int? limit = null;
        if (Match(TokenType.Limit)) limit = int.Parse(Expect(TokenType.Number).Text, CultureInfo.InvariantCulture);

        Match(TokenType.Semicolon);
        Expect(TokenType.Eof);

        return new SelectStatement(columns, from, joins, where, groupBy, having, orderBy, limit);
    }

    // create_index_stmt := CREATE INDEX ON IDENTIFIER LPAREN IDENTIFIER RPAREN (USING (HASH|BTREE))? SEMICOLON? EOF
    private CreateIndexStatement ParseCreateIndexStatement()
    {
        Expect(TokenType.Create);
        Expect(TokenType.Index);
        Expect(TokenType.On);
        var table = Expect(TokenType.Identifier).Text;
        Expect(TokenType.LParen);
        var column = Expect(TokenType.Identifier).Text;
        Expect(TokenType.RParen);

        var kind = IndexTypeHint.BTree;
        if (Match(TokenType.Using))
        {
            var kindToken = Expect(TokenType.Identifier).Text;
            kind = kindToken.ToUpperInvariant() switch
            {
                "HASH" => IndexTypeHint.Hash,
                "BTREE" => IndexTypeHint.BTree,
                _ => throw Error($"unknown index type '{kindToken}' — expected HASH or BTREE")
            };
        }

        Match(TokenType.Semicolon);
        Expect(TokenType.Eof);
        return new CreateIndexStatement(table, column, kind);
    }

    private List<SelectItem> ParseSelectList()
    {
        var items = new List<SelectItem>();
        if (Current.Type == TokenType.Star)
        {
            Advance();
            items.Add(new SelectItem(IsStar: true, Expression: null, Alias: null));
            return items;
        }

        items.Add(ParseSelectItem());
        while (Match(TokenType.Comma)) items.Add(ParseSelectItem());
        return items;
    }

    private SelectItem ParseSelectItem()
    {
        var expr = IsAggregateCallStart() ? ParseAggregateCall() : (Expr)ParseColumnRef();
        string? alias = null;
        if (Match(TokenType.As)) alias = Expect(TokenType.Identifier).Text;
        return new SelectItem(IsStar: false, expr, alias);
    }

    private OrderByItem ParseOrderByItem()
    {
        var column = ParseColumnRef();
        var descending = false;
        if (Match(TokenType.Desc)) descending = true;
        else Match(TokenType.Asc);
        return new OrderByItem(column, descending);
    }

    private TableRef ParseTableRef()
    {
        var name = Expect(TokenType.Identifier).Text;
        string? alias = null;
        if (Match(TokenType.As)) alias = Expect(TokenType.Identifier).Text;
        else if (Current.Type == TokenType.Identifier) alias = Advance().Text;
        return new TableRef(name, alias);
    }

    private bool IsAggregateCallStart() =>
        Current.Type == TokenType.Identifier
        && AggregateFunctionNames.Contains(Current.Text)
        && PeekToken().Type == TokenType.LParen;

    // agg_call := ('COUNT' | 'SUM' | 'AVG' | 'MIN' | 'MAX') LPAREN (STAR | column_ref) RPAREN
    private AggregateExpr ParseAggregateCall()
    {
        var funcToken = Advance();
        var func = Enum.Parse<AggregateFunc>(funcToken.Text, ignoreCase: true);
        Expect(TokenType.LParen);

        if (Match(TokenType.Star))
        {
            if (func != AggregateFunc.Count) throw Error($"only COUNT supports '*', not {func.ToString().ToUpperInvariant()}");
            Expect(TokenType.RParen);
            return new AggregateExpr(func, Argument: null, IsCountStar: true);
        }

        var arg = ParseColumnRef();
        Expect(TokenType.RParen);
        return new AggregateExpr(func, arg, IsCountStar: false);
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

        return left; // bare BOOL-typed operand, e.g. `WHERE is_active`, or `HAVING SUM(x) ...` handled above
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

    // operand := NUMBER | STRING | TRUE | FALSE | agg_call | column_ref
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
                return IsAggregateCallStart() ? ParseAggregateCall() : ParseColumnRef();
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
