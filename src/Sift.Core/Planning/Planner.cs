using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Planning;

/// <summary>
/// Translates a parsed SELECT into a logical plan, then compiles that into an executable
/// Operator tree — the "physical plan" for Phase 2. There's no cost-based choice yet (Phase 3
/// adds SeqScan-vs-IndexScan and predicate pushdown); every logical node maps to exactly one
/// physical strategy today, so a separate PhysicalPlan IR would just mirror Operator with no
/// alternatives to represent — it earns its keep once there's a choice to make.
/// </summary>
public static class Planner
{
    public static Operator Plan(SelectStatement stmt, Catalog.Catalog catalog) =>
        Compile(BuildLogicalPlan(stmt), catalog);

    public static LogicalPlan BuildLogicalPlan(SelectStatement stmt)
    {
        LogicalPlan plan = new LogicalScan(stmt.From);
        foreach (var join in stmt.Joins)
            plan = new LogicalJoin(plan, new LogicalScan(join.Table), join);

        if (stmt.Where is not null) plan = new LogicalFilter(plan, stmt.Where);

        var aggregates = CollectAggregates(stmt);
        if (stmt.GroupBy.Count > 0 || aggregates.Count > 0)
        {
            plan = new LogicalAggregate(plan, stmt.GroupBy, aggregates);
            if (stmt.Having is not null) plan = new LogicalFilter(plan, stmt.Having);
        }

        // Sort runs before Project so ORDER BY can reference any source/aggregate column, not
        // just ones in the SELECT list. That means a SELECT alias (e.g. `AS total`) isn't in
        // scope yet at this point, so alias references are translated back to their underlying
        // column/aggregate name first.
        if (stmt.OrderBy.Count > 0) plan = new LogicalSort(plan, ResolveOrderByAliases(stmt));

        plan = new LogicalProject(plan, stmt.Columns);

        if (stmt.Limit is { } limit) plan = new LogicalLimit(plan, limit);

        return plan;
    }

    private static List<OrderByItem> ResolveOrderByAliases(SelectStatement stmt)
    {
        var aliasToUnderlyingName = stmt.Columns
            .Where(c => !c.IsStar && c.Alias is not null)
            .ToDictionary(c => c.Alias!, c => c.Expression switch
            {
                ColumnRefExpr col => col.ColumnName,
                AggregateExpr agg => agg.CanonicalName,
                _ => throw new InvalidOperationException($"select item '{c.Expression}' has no resolvable name")
            }, StringComparer.OrdinalIgnoreCase);

        return stmt.OrderBy.Select(item =>
        {
            if (!aliasToUnderlyingName.TryGetValue(item.Column.ColumnName, out var underlyingName)) return item;
            return item with { Column = item.Column with { ColumnName = underlyingName } };
        }).ToList();
    }

    private static List<AggregateExpr> CollectAggregates(SelectStatement stmt)
    {
        var found = new List<AggregateExpr>();
        void Visit(Expr? expr)
        {
            switch (expr)
            {
                case AggregateExpr agg: found.Add(agg); break;
                case LogicalExpr l: Visit(l.Left); Visit(l.Right); break;
                case NotExpr n: Visit(n.Operand); break;
                case ComparisonExpr c: Visit(c.Left); Visit(c.Right); break;
                case BetweenExpr b: Visit(b.Value); Visit(b.Low); Visit(b.High); break;
                case InExpr inExpr: Visit(inExpr.Value); foreach (var item in inExpr.Items) Visit(item); break;
                case IsNullExpr isNull: Visit(isNull.Operand); break;
            }
        }

        foreach (var item in stmt.Columns)
            if (!item.IsStar) Visit(item.Expression);
        Visit(stmt.Having);

        var seen = new HashSet<string>();
        return found.Where(a => seen.Add(a.CanonicalName)).ToList();
    }

    private static Operator Compile(LogicalPlan plan, Catalog.Catalog catalog)
    {
        switch (plan)
        {
            case LogicalScan scan:
                return new SeqScan(catalog.GetTable(scan.Table.Name));

            case LogicalJoin join:
                var left = Compile(join.Left, catalog);
                var right = Compile(join.Right, catalog);
                var leftIndex = left.OutputSchema.IndexOf(join.Clause.LeftColumn.ColumnName);
                var rightIndex = right.OutputSchema.IndexOf(join.Clause.RightColumn.ColumnName);
                return new HashJoin(left, right, leftIndex, rightIndex);

            case LogicalFilter filter:
                var filterChild = Compile(filter.Input, catalog);
                return new Filter(filterChild, new ExprPredicate(filter.Predicate, filterChild.OutputSchema));

            case LogicalAggregate aggregate:
                var aggChild = Compile(aggregate.Input, catalog);
                var groupByIndices = aggregate.GroupBy.Select(c => aggChild.OutputSchema.IndexOf(c.ColumnName)).ToArray();
                var specs = aggregate.Aggregates.Select(a => BuildAggregateSpec(a, aggChild.OutputSchema)).ToArray();
                return new HashAggregate(aggChild, groupByIndices, specs);

            case LogicalProject project:
                return BuildProject(Compile(project.Input, catalog), project.Items);

            case LogicalSort sort:
                var sortChild = Compile(sort.Input, catalog);
                var keys = sort.OrderBy
                    .Select(o => new SortKey(sortChild.OutputSchema.IndexOf(o.Column.ColumnName), o.Descending))
                    .ToArray();
                return new Sort(sortChild, keys);

            case LogicalLimit limit:
                return new Limit(Compile(limit.Input, catalog), limit.Count);

            default:
                throw new InvalidOperationException($"unreachable logical plan node: {plan}");
        }
    }

    private static AggregateSpec BuildAggregateSpec(AggregateExpr expr, Schema inputSchema)
    {
        var argIndex = expr.IsCountStar ? -1 : inputSchema.IndexOf(expr.Argument!.ColumnName);
        var outputType = expr.Func switch
        {
            AggregateFunc.Count => SqlType.Int,
            AggregateFunc.Sum or AggregateFunc.Avg => SqlType.Decimal,
            AggregateFunc.Min or AggregateFunc.Max => inputSchema.Columns[argIndex].Type,
            _ => throw new InvalidOperationException($"unreachable: {expr.Func}")
        };
        return new AggregateSpec(expr.Func, argIndex, expr.IsCountStar, expr.CanonicalName, outputType);
    }

    private static Operator BuildProject(Operator child, IReadOnlyList<SelectItem> items)
    {
        if (items.Count == 1 && items[0].IsStar)
        {
            var identity = Enumerable.Range(0, child.OutputSchema.Columns.Count).ToArray();
            return new Project(child, child.OutputSchema, identity);
        }

        var columns = new Column[items.Count];
        var sourceIndices = new int[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            var resolveKey = item.Expression switch
            {
                ColumnRefExpr col => col.ColumnName,
                AggregateExpr agg => agg.CanonicalName,
                _ => throw new InvalidOperationException($"select item '{item.Expression}' cannot be projected")
            };
            sourceIndices[i] = child.OutputSchema.IndexOf(resolveKey);
            columns[i] = new Column(item.Alias ?? resolveKey, child.OutputSchema.Columns[sourceIndices[i]].Type);
        }
        return new Project(child, new Schema(columns), sourceIndices);
    }
}
