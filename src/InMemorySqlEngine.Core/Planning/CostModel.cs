using InMemorySqlEngine.Core.Sql.Ast;

namespace InMemorySqlEngine.Core.Planning;

/// <summary>
/// The core of the cost-based planner (docs/design.md §5): a sequential scan pays a small cost per row with
/// good locality; an index scan pays a tree-traversal cost once plus a random-access cost per
/// *matched* row. When a predicate isn't selective — it matches most of the table — the random
/// access adds up past what one sequential sweep would have cost, and the planner should refuse
/// its own index. That comparison is exactly what <see cref="IndexScanCost"/> vs
/// <see cref="SeqScanCost"/> makes possible.
/// </summary>
public static class CostModel
{
    public const double SeqScanCostPerRow = 1.0;
    public const double RandomAccessCostPerRow = 4.0;
    public const double TreeTraversalCostPerLevel = 2.0;
    public const double DefaultRangeSelectivity = 0.3;

    /// <summary>Residual filters (after the chosen index conjunct, or with no index at all) get no
    /// per-predicate selectivity estimate — just this flat default. Documented approximation.</summary>
    public const double DefaultFilterSelectivity = 0.5;
    public const double FilterCostPerRow = 0.2;
    public const double ProjectCostPerRow = 0.05;
    public const double HashBuildOrProbeCostPerRow = 1.0;
    public const double NestedLoopCostPerPair = 1.0;
    public const double SortCostPerRowPerLevel = 0.5;
    public const double HashAggregateCostPerRow = 1.5;

    /// <summary>No GROUP BY columns → the aggregate has read the grouping cardinality
    /// exactly (there's exactly one group). With GROUP BY columns, no histogram means no real
    /// way to know how many distinct combinations exist without scanning — this flat fraction
    /// of the input row count is a deliberately crude stand-in.</summary>
    public const double DefaultGroupingFraction = 0.1;

    public static double SeqScanCost(int rowCount) => rowCount * SeqScanCostPerRow;

    public static double TreeTraversalCost(int rowCount) => TreeTraversalCostPerLevel * Math.Log2(Math.Max(rowCount, 2));

    public static double IndexScanCost(int rowCount, double selectivity)
    {
        var matchedRows = rowCount * selectivity;
        return TreeTraversalCost(rowCount) + matchedRows * RandomAccessCostPerRow;
    }

    public static double HashJoinCost(double leftRows, double rightRows) => (leftRows + rightRows) * HashBuildOrProbeCostPerRow;

    public static double NestedLoopJoinCost(double leftRows, double rightRows) => leftRows * rightRows * NestedLoopCostPerPair;

    /// <summary>Standard containment-assumption join cardinality estimate: each row on the
    /// larger side of distinctness finds roughly (other side's row count / that side's distinct
    /// values) matches. Ignores that a filter upstream may have already narrowed the real
    /// distinct count — an approximation flagged rather than silently assumed away.</summary>
    public static double EstimateJoinRowCount(double leftRows, double rightRows, int leftDistinct, int rightDistinct)
    {
        var maxDistinct = Math.Max(Math.Max(leftDistinct, rightDistinct), 1);
        return leftRows * rightRows / maxDistinct;
    }

    public static double SortCost(double rowCount) => rowCount * SortCostPerRowPerLevel * Math.Log2(Math.Max(rowCount, 2));

    /// <summary>
    /// Fraction of rows an operator is expected to match. `=` scales with how many distinct
    /// values share the column (a unique key matches ~1 row; a boolean flag matches ~half the
    /// table); everything else falls back to a flat estimate — real systems use histograms to
    /// do better on skewed data or ranges, which is the natural next step here.
    /// </summary>
    public static double EstimateSelectivity(ComparisonOp op, int distinctValues)
    {
        var distinct = Math.Max(distinctValues, 1);
        return op switch
        {
            ComparisonOp.Eq => 1.0 / distinct,
            ComparisonOp.NotEq => 1.0 - 1.0 / distinct,
            _ => DefaultRangeSelectivity // Lt, LtEq, Gt, GtEq, and BETWEEN (computed by the caller)
        };
    }
}
