using Sift.Core.Planning;
using Sift.Core.Sql.Ast;
using Xunit;

namespace Sift.Tests.Planning;

public class CostModelTests
{
    [Fact]
    public void EqualitySelectivity_IsInverseOfDistinctCount()
    {
        Assert.Equal(0.5, CostModel.EstimateSelectivity(ComparisonOp.Eq, distinctValues: 2));
        Assert.Equal(0.001, CostModel.EstimateSelectivity(ComparisonOp.Eq, distinctValues: 1000), precision: 6);
    }

    [Fact]
    public void RangeSelectivity_UsesFlatDefault()
    {
        Assert.Equal(CostModel.DefaultRangeSelectivity, CostModel.EstimateSelectivity(ComparisonOp.Lt, distinctValues: 5));
        Assert.Equal(CostModel.DefaultRangeSelectivity, CostModel.EstimateSelectivity(ComparisonOp.GtEq, distinctValues: 5000));
    }

    /// <summary>The headline claim (docs/design.md §5): a low-selectivity equality predicate should
    /// cost MORE via an index than a plain scan, and a high-selectivity one should cost LESS.</summary>
    [Fact]
    public void IndexScanCost_CrossesOverWithSelectivity()
    {
        const int rowCount = 190_757;

        var lowSelectivity = CostModel.EstimateSelectivity(ComparisonOp.Eq, distinctValues: 2); // e.g. a boolean flag
        var highSelectivity = CostModel.EstimateSelectivity(ComparisonOp.Eq, distinctValues: 30); // e.g. a near-unique sku

        var seqCost = CostModel.SeqScanCost(rowCount);
        var lowSelIndexCost = CostModel.IndexScanCost(rowCount, lowSelectivity);
        var highSelIndexCost = CostModel.IndexScanCost(rowCount, highSelectivity);

        Assert.True(lowSelIndexCost > seqCost, "a near-half-the-table equality match should NOT be cheaper via an index");
        Assert.True(highSelIndexCost < seqCost, "a ~1-in-30 equality match should be cheaper via an index");
    }

    [Fact]
    public void SeqScanCost_ScalesLinearlyWithRowCount()
    {
        Assert.Equal(CostModel.SeqScanCost(1000) * 2, CostModel.SeqScanCost(2000), precision: 6);
    }
}
