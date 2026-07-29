using Sift.Core.Values;

namespace Sift.Core.Sql.Ast;

public abstract record Expr;

public sealed record ColumnRefExpr(string? Qualifier, string ColumnName) : Expr
{
    public override string ToString() => Qualifier is null ? ColumnName : $"{Qualifier}.{ColumnName}";
}

public sealed record LiteralExpr(SqlValue Value) : Expr
{
    public override string ToString() => Value.ToString();
}

public enum ComparisonOp { Eq, NotEq, Lt, LtEq, Gt, GtEq }

public sealed record ComparisonExpr(Expr Left, ComparisonOp Op, Expr Right) : Expr;

public enum LogicalOp { And, Or }

public sealed record LogicalExpr(Expr Left, LogicalOp Op, Expr Right) : Expr;

public sealed record NotExpr(Expr Operand) : Expr;

/// <summary>`value BETWEEN low AND high` — inclusive on both ends, per standard SQL.</summary>
public sealed record BetweenExpr(Expr Value, Expr Low, Expr High) : Expr;

public sealed record InExpr(Expr Value, IReadOnlyList<Expr> Items) : Expr;

public sealed record IsNullExpr(Expr Operand, bool Negated) : Expr;
