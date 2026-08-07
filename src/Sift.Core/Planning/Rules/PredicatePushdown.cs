using Sift.Core.Sql.Ast;

namespace Sift.Core.Planning.Rules;

/// <summary>
/// Splits a WHERE clause into its AND-conjuncts and moves each one as close to the base table
/// scan(s) it references as possible — specifically, below a JOIN instead of above it, so a
/// join only ever processes rows that already passed their own table's filters (PLAN.md §3, §6).
/// A conjunct referencing columns from both sides of a join (or that can't be attributed to one
/// side) stays above the join, unchanged.
/// </summary>
public static class PredicatePushdown
{
    private readonly record struct Side(HashSet<string> TableNames, HashSet<string> ColumnNames);

    public static LogicalPlan Apply(LogicalPlan plan, Catalog.Catalog catalog)
    {
        switch (plan)
        {
            case LogicalFilter filter:
                var input = Apply(filter.Input, catalog);
                return PushIntoJoin(input, filter.Predicate, catalog);

            case LogicalJoin join:
                return join with { Left = Apply(join.Left, catalog), Right = Apply(join.Right, catalog) };

            case LogicalAggregate agg:
                return agg with { Input = Apply(agg.Input, catalog) };

            case LogicalProject project:
                return project with { Input = Apply(project.Input, catalog) };

            case LogicalSort sort:
                return sort with { Input = Apply(sort.Input, catalog) };

            case LogicalLimit limit:
                return limit with { Input = Apply(limit.Input, catalog) };

            default:
                return plan; // LogicalScan — nothing below it to push into
        }
    }

    private static LogicalPlan PushIntoJoin(LogicalPlan input, Expr predicate, Catalog.Catalog catalog)
    {
        if (input is not LogicalJoin join) return new LogicalFilter(input, predicate);

        var leftSide = CollectSide(join.Left, catalog);
        var rightSide = CollectSide(join.Right, catalog);

        var left = join.Left;
        var right = join.Right;
        var remaining = new List<Expr>();

        foreach (var conjunct in ExprAnalysis.SplitConjuncts(predicate))
        {
            var refs = ReferencedColumnRefs(conjunct);
            if (refs.Count > 0 && refs.All(r => BelongsTo(r, leftSide)) && !refs.Any(r => BelongsTo(r, rightSide)))
                left = new LogicalFilter(left, conjunct);
            else if (refs.Count > 0 && refs.All(r => BelongsTo(r, rightSide)) && !refs.Any(r => BelongsTo(r, leftSide)))
                right = new LogicalFilter(right, conjunct);
            else
                remaining.Add(conjunct);
        }

        LogicalPlan result = join with { Left = left, Right = right };
        return remaining.Count == 0 ? result : new LogicalFilter(result, ExprAnalysis.Combine(remaining));
    }

    /// <summary>
    /// A qualified reference (`b.category`) belongs to a side only if that side has a scan
    /// aliased/named `b` — this is what stops an unrelated same-named column on the *other*
    /// side (e.g. both tables happening to have a `category` column) from being mismatched.
    /// An unqualified reference belongs to a side only if it's unambiguous there.
    /// </summary>
    private static bool BelongsTo(ColumnRefExpr colRef, Side side) => colRef.Qualifier is { } qualifier
        ? side.TableNames.Contains(qualifier)
        : side.ColumnNames.Contains(colRef.ColumnName);

    private static List<ColumnRefExpr> ReferencedColumnRefs(Expr expr)
    {
        var found = new List<ColumnRefExpr>();
        void Visit(Expr? e)
        {
            switch (e)
            {
                case ColumnRefExpr col: found.Add(col); break;
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

    private static Side CollectSide(LogicalPlan plan, Catalog.Catalog catalog)
    {
        var tableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Visit(LogicalPlan p)
        {
            switch (p)
            {
                case LogicalScan scan:
                    tableNames.Add(scan.Table.EffectiveName);
                    tableNames.Add(scan.Table.Name);
                    foreach (var c in catalog.GetTable(scan.Table.Name).Schema.Columns) columnNames.Add(c.Name);
                    break;
                case LogicalJoin join: Visit(join.Left); Visit(join.Right); break;
                case LogicalFilter filter: Visit(filter.Input); break;
            }
        }
        Visit(plan);
        return new Side(tableNames, columnNames);
    }
}
