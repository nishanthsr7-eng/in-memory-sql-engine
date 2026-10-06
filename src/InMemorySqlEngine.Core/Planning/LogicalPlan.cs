using InMemorySqlEngine.Core.Sql.Ast;

namespace InMemorySqlEngine.Core.Planning;

/// <summary>
/// WHAT to compute — a plan node deliberately kept distinct from the physical Operator tree
/// (HOW) it eventually becomes, so a rule-based rewrite (predicate pushdown) has a
/// tree to rewrite before any physical strategy — index vs. scan, hash vs. nested-loop join —
/// gets chosen.
/// </summary>
public abstract record LogicalPlan;

public sealed record LogicalScan(TableRef Table) : LogicalPlan;

public sealed record LogicalJoin(LogicalPlan Left, LogicalPlan Right, JoinClause Clause) : LogicalPlan;

public sealed record LogicalFilter(LogicalPlan Input, Expr Predicate) : LogicalPlan;

public sealed record LogicalAggregate(
    LogicalPlan Input,
    IReadOnlyList<ColumnRefExpr> GroupBy,
    IReadOnlyList<AggregateExpr> Aggregates) : LogicalPlan;

public sealed record LogicalProject(LogicalPlan Input, IReadOnlyList<SelectItem> Items) : LogicalPlan;

public sealed record LogicalSort(LogicalPlan Input, IReadOnlyList<OrderByItem> OrderBy) : LogicalPlan;

public sealed record LogicalLimit(LogicalPlan Input, int Count) : LogicalPlan;
