using Sift.Core.Sql.Ast;

namespace Sift.Core.Planning;

/// <summary>
/// The whole point of Phase 3 (PLAN.md §3): a sequential scan pays a small cost per row with
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

    public static double SeqScanCost(int rowCount) => rowCount * SeqScanCostPerRow;

    public static double TreeTraversalCost(int rowCount) => TreeTraversalCostPerLevel * Math.Log2(Math.Max(rowCount, 2));

    public static double IndexScanCost(int rowCount, double selectivity)
    {
        var matchedRows = rowCount * selectivity;
        return TreeTraversalCost(rowCount) + matchedRows * RandomAccessCostPerRow;
    }

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
