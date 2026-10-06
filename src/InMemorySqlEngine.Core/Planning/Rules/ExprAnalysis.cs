using InMemorySqlEngine.Core.Sql.Ast;

namespace InMemorySqlEngine.Core.Planning.Rules;

/// <summary>Small AST-walking helpers shared by predicate pushdown and index-candidate selection.</summary>
internal static class ExprAnalysis
{
    /// <summary>Flattens a top-level AND-chain into its conjuncts (`a AND b AND c` → [a, b, c]); a non-AND expression is its own single conjunct.</summary>
    public static List<Expr> SplitConjuncts(Expr expr)
    {
        var result = new List<Expr>();
        void Visit(Expr e)
        {
            if (e is LogicalExpr { Op: LogicalOp.And } and) { Visit(and.Left); Visit(and.Right); }
            else result.Add(e);
        }
        Visit(expr);
        return result;
    }

    public static Expr Combine(IReadOnlyList<Expr> conjuncts) =>
        conjuncts.Aggregate((a, b) => new LogicalExpr(a, LogicalOp.And, b));

    public static HashSet<string> ReferencedColumns(Expr expr)
    {
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Visit(Expr? e)
        {
            switch (e)
            {
                case ColumnRefExpr col: found.Add(col.ColumnName); break;
                case LogicalExpr l: Visit(l.Left); Visit(l.Right); break;
                case NotExpr n: Visit(n.Operand); break;
                case ComparisonExpr c: Visit(c.Left); Visit(c.Right); break;
                case BetweenExpr b: Visit(b.Value); Visit(b.Low); Visit(b.High); break;
                case InExpr i: Visit(i.Value); foreach (var item in i.Items) Visit(item); break;
                case IsNullExpr isNull: Visit(isNull.Operand); break;
                case AggregateExpr { Argument: { } arg }: Visit(arg); break;
            }
        }
        Visit(expr);
        return found;
    }
}
