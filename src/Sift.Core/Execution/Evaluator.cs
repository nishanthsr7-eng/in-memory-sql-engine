using System.Globalization;
using Sift.Core.Catalog;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Execution;

/// <summary>
/// Evaluates AST expressions against a row. Split into two entry points because SQL has two
/// separate expression worlds: scalar values (SELECT list) and three-valued predicates (WHERE).
/// </summary>
public static class Evaluator
{
    public static SqlValue Eval(Expr expr, Row row, Schema schema)
    {
        return expr switch
        {
            ColumnRefExpr colRef => row[schema.IndexOf(colRef.ColumnName)],
            LiteralExpr lit => lit.Value,
            // Already computed by HashAggregate, whose output column is named by canonical
            // name — a HAVING clause repeating the same aggregate call is just a lookup by then.
            AggregateExpr agg => row[schema.IndexOf(agg.CanonicalName)],
            _ => throw new InvalidOperationException($"'{expr}' is not a scalar expression")
        };
    }

    public static SqlBool EvalPredicate(Expr expr, Row row, Schema schema)
    {
        switch (expr)
        {
            case LogicalExpr logical:
                var left = EvalPredicate(logical.Left, row, schema);
                var right = EvalPredicate(logical.Right, row, schema);
                return logical.Op == LogicalOp.And ? left.And(right) : left.Or(right);

            case NotExpr not:
                return EvalPredicate(not.Operand, row, schema).Not();

            case ComparisonExpr cmp:
                var (a, b) = AlignTypes(Eval(cmp.Left, row, schema), Eval(cmp.Right, row, schema));
                return cmp.Op switch
                {
                    ComparisonOp.Eq => a.EqualsSql(b),
                    ComparisonOp.NotEq => a.NotEqualsSql(b),
                    ComparisonOp.Lt => a.LessThan(b),
                    ComparisonOp.LtEq => a.LessThanOrEqual(b),
                    ComparisonOp.Gt => a.GreaterThan(b),
                    ComparisonOp.GtEq => a.GreaterThanOrEqual(b),
                    _ => throw new InvalidOperationException($"unreachable: {cmp.Op}")
                };

            case BetweenExpr between:
                var value = Eval(between.Value, row, schema);
                var (v1, lo) = AlignTypes(value, Eval(between.Low, row, schema));
                var (v2, hi) = AlignTypes(value, Eval(between.High, row, schema));
                return v1.GreaterThanOrEqual(lo).And(v2.LessThanOrEqual(hi));

            case InExpr inExpr:
                var target = Eval(inExpr.Value, row, schema);
                var result = SqlBool.False;
                foreach (var itemExpr in inExpr.Items)
                {
                    var (t, item) = AlignTypes(target, Eval(itemExpr, row, schema));
                    result = result.Or(t.EqualsSql(item));
                }
                return result;

            case IsNullExpr isNull:
                var operand = Eval(isNull.Operand, row, schema);
                return isNull.Negated ? operand.IsNotNullSql() : operand.IsNullSql();

            default:
                var scalar = Eval(expr, row, schema);
                if (scalar.Type != SqlType.Bool)
                    throw new InvalidOperationException($"'{expr}' is not a boolean expression");
                return scalar.IsNull ? SqlBool.Unknown : SqlBoolExtensions.FromBool(scalar.AsBool);
        }
    }

    /// <summary>
    /// Coerces a TEXT literal to whatever concrete type it's being compared against (e.g. a
    /// date literal typed as a string by the parser, compared against a DATE column). Only
    /// literals move — column values are never reinterpreted.
    /// </summary>
    private static (SqlValue, SqlValue) AlignTypes(SqlValue a, SqlValue b)
    {
        if (a.Type == b.Type || a.IsNull || b.IsNull) return (a, b);
        if (a.Type == SqlType.Text) return (CoerceText(a, b.Type), b);
        if (b.Type == SqlType.Text) return (a, CoerceText(b, a.Type));
        return (a, b); // numeric Int/Decimal mix — SqlValue compares those directly
    }

    private static SqlValue CoerceText(SqlValue textValue, SqlType targetType)
    {
        var text = textValue.AsText;
        return targetType switch
        {
            SqlType.Date => SqlValue.Date(DateTime.Parse(text, CultureInfo.InvariantCulture)),
            SqlType.Int => SqlValue.Int(long.Parse(text, CultureInfo.InvariantCulture)),
            SqlType.Decimal => SqlValue.Decimal(decimal.Parse(text, NumberStyles.Float, CultureInfo.InvariantCulture)),
            SqlType.Bool => SqlValue.Bool(bool.Parse(text)),
            _ => textValue
        };
    }
}
