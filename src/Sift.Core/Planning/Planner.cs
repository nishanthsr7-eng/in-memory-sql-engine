using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Indexing;
using Sift.Core.Planning.Rules;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Planning;

/// <summary>
/// Translates a parsed SELECT into a logical plan, rewrites it (predicate pushdown), then
/// compiles the result into an executable Operator tree. The one real choice — index scan vs.
/// sequential scan — is made where a Filter sits directly over a Scan, by comparing
/// <see cref="CostModel"/> estimates; every other logical node still maps to exactly one
/// physical strategy, so a separate PhysicalPlan IR would mostly mirror Operator with nothing
/// to represent — it can earn its keep once there's a second axis of choice (e.g. join order).
/// </summary>
public static class Planner
{
    public static Operator Plan(SelectStatement stmt, Catalog.Catalog catalog)
    {
        var logical = PredicatePushdown.Apply(BuildLogicalPlan(stmt), catalog);
        return Compile(logical, catalog);
    }

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

                var leftDistinct = FindBaseTable(join.Left, catalog)?.Statistics.DistinctCount(join.Clause.LeftColumn.ColumnName) ?? 1;
                var rightDistinct = FindBaseTable(join.Right, catalog)?.Statistics.DistinctCount(join.Clause.RightColumn.ColumnName) ?? 1;
                var joinEstRows = CostModel.EstimateJoinRowCount(left.EstimatedRowCount, right.EstimatedRowCount, leftDistinct, rightDistinct);

                return new HashJoin(left, right, leftIndex, rightIndex, joinEstRows);

            case LogicalFilter { Input: LogicalScan scan } filter:
                return CompileScanWithFilter(scan, filter.Predicate, catalog);

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

    /// <summary>Descends through pushed-down Filters to the Scan they sit on — used only to
    /// look up base-table Statistics for join cardinality estimates (an approximation: it
    /// ignores that those Filters may have already narrowed the real distinct count).</summary>
    private static Table? FindBaseTable(LogicalPlan plan, Catalog.Catalog catalog) => plan switch
    {
        LogicalScan scan => catalog.GetTable(scan.Table.Name),
        LogicalFilter filter => FindBaseTable(filter.Input, catalog),
        _ => null
    };

    private readonly record struct IndexCandidate(Expr Conjunct, IIndex Index, IndexCondition Condition, double EstimatedRows, double Cost);

    /// <summary>
    /// The cost-based decision (PLAN.md §3, §11 differentiator #1): split the WHERE clause into
    /// conjuncts, find the cheapest one an index can answer, and only use it if it actually
    /// beats a sequential scan — a predicate matching most of the table (e.g. a boolean flag)
    /// correctly loses to SeqScan even when an index on that column exists, because the index's
    /// per-row random access adds up past what one sequential sweep costs.
    /// </summary>
    private static Operator CompileScanWithFilter(LogicalScan scan, Expr predicate, Catalog.Catalog catalog)
    {
        var table = catalog.GetTable(scan.Table.Name);
        var conjuncts = ExprAnalysis.SplitConjuncts(predicate);

        IndexCandidate? best = null;
        foreach (var conjunct in conjuncts)
        {
            var candidate = TryBuildIndexCandidate(conjunct, table, catalog);
            if (candidate is { } c && (best is not { } b || c.Cost < b.Cost)) best = c;
        }

        var seqCost = CostModel.SeqScanCost(table.RowCount);

        Operator baseOp;
        Expr? residual;
        if (best is { } chosen && chosen.Cost < seqCost)
        {
            baseOp = new IndexScan(table, chosen.Index, chosen.Condition, chosen.EstimatedRows, chosen.Cost);
            var rest = conjuncts.Where(c => !ReferenceEquals(c, chosen.Conjunct)).ToList();
            residual = rest.Count == 0 ? null : ExprAnalysis.Combine(rest);
        }
        else
        {
            baseOp = new SeqScan(table);
            residual = predicate;
        }

        return residual is null ? baseOp : new Filter(baseOp, new ExprPredicate(residual, baseOp.OutputSchema));
    }

    private static IndexCandidate? TryBuildIndexCandidate(Expr conjunct, Table table, Catalog.Catalog catalog)
    {
        switch (conjunct)
        {
            case ComparisonExpr { Left: ColumnRefExpr col, Right: LiteralExpr lit } cmp:
                return BuildComparisonCandidate(conjunct, col.ColumnName, cmp.Op, lit.Value, table, catalog);

            // literal-on-the-left form (`5 = price`) — flip the operator so it reads column-relative
            case ComparisonExpr { Left: LiteralExpr lit, Right: ColumnRefExpr col } cmp:
                return BuildComparisonCandidate(conjunct, col.ColumnName, Flip(cmp.Op), lit.Value, table, catalog);

            case BetweenExpr { Value: ColumnRefExpr col, Low: LiteralExpr lo, High: LiteralExpr hi }:
                if (!catalog.TryGetIndex(table.Name, col.ColumnName, out var betweenIndex) || !betweenIndex.SupportsRange) return null;
                var betweenSelectivity = CostModel.DefaultRangeSelectivity;
                var betweenCost = CostModel.IndexScanCost(table.RowCount, betweenSelectivity);
                var betweenCondition = new IndexCondition.Range(lo.Value, true, hi.Value, true);
                return new IndexCandidate(conjunct, betweenIndex, betweenCondition, table.RowCount * betweenSelectivity, betweenCost);

            default:
                return null; // IN / IS NULL / compound expressions — not index candidates, handled by the residual Filter
        }
    }

    private static IndexCandidate? BuildComparisonCandidate(
        Expr conjunct, string columnName, ComparisonOp op, SqlValue literal, Table table, Catalog.Catalog catalog)
    {
        if (op == ComparisonOp.NotEq) return null; // not expressible as a single index range
        if (!catalog.TryGetIndex(table.Name, columnName, out var index)) return null;
        if (op != ComparisonOp.Eq && !index.SupportsRange) return null;

        if (op == ComparisonOp.Eq)
        {
            var selectivity = CostModel.EstimateSelectivity(op, table.Statistics.DistinctCount(columnName));
            var cost = CostModel.IndexScanCost(table.RowCount, selectivity);
            return new IndexCandidate(conjunct, index, new IndexCondition.Exact(literal), table.RowCount * selectivity, cost);
        }

        var rangeSelectivity = CostModel.EstimateSelectivity(op, table.Statistics.DistinctCount(columnName));
        var rangeCost = CostModel.IndexScanCost(table.RowCount, rangeSelectivity);
        var condition = op switch
        {
            ComparisonOp.Lt => new IndexCondition.Range(null, false, literal, false),
            ComparisonOp.LtEq => new IndexCondition.Range(null, false, literal, true),
            ComparisonOp.Gt => new IndexCondition.Range(literal, false, null, false),
            ComparisonOp.GtEq => new IndexCondition.Range(literal, true, null, false),
            _ => throw new InvalidOperationException($"unreachable: {op}")
        };
        return new IndexCandidate(conjunct, index, condition, table.RowCount * rangeSelectivity, rangeCost);
    }

    private static ComparisonOp Flip(ComparisonOp op) => op switch
    {
        ComparisonOp.Lt => ComparisonOp.Gt,
        ComparisonOp.LtEq => ComparisonOp.GtEq,
        ComparisonOp.Gt => ComparisonOp.Lt,
        ComparisonOp.GtEq => ComparisonOp.LtEq,
        var same => same // Eq / NotEq are symmetric
    };

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
