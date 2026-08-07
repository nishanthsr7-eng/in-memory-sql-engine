using Sift.Core.Catalog;
using Sift.Core.Indexing;
using Sift.Core.Planning;
using Sift.Core.Sql;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Planning;

/// <summary>
/// Phase 3's headline behavior (PLAN.md §3 "Done when"): the planner picks SeqScan over its own
/// index for a low-selectivity predicate, and IndexScan for a high-selectivity one — verified
/// end to end through real parsed SQL and a real Catalog, not just the CostModel formulas.
/// </summary>
public class CostBasedScanSelectionTests
{
    private static Sift.Core.Catalog.Catalog BuildCatalog(int rowCount = 2000)
    {
        var schema = new Schema(new[]
        {
            new Column("sku", SqlType.Text),      // near-unique: high selectivity
            new Column("promotion_flag", SqlType.Int), // binary: low selectivity
        });
        var rows = new Row[rowCount];
        for (var i = 0; i < rowCount; i++)
            rows[i] = new Row(new[] { SqlValue.Text($"SKU-{i}"), SqlValue.Int(i % 2) });

        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Table("t", schema, rows));
        catalog.CreateIndex("t", "sku", IndexKind.BPlusTree);
        catalog.CreateIndex("t", "promotion_flag", IndexKind.BPlusTree);
        return catalog;
    }

    private static string PlanExplain(string sql, Sift.Core.Catalog.Catalog catalog) =>
        Planner.Plan(Parser.ParseSelect(sql), catalog).Explain(0);

    [Fact]
    public void HighSelectivityEquality_ChoosesIndexScan()
    {
        var explain = PlanExplain("SELECT * FROM t WHERE sku = 'SKU-500'", BuildCatalog());
        Assert.Contains("IndexScan", explain);
    }

    [Fact]
    public void LowSelectivityEquality_ChoosesSeqScan_EvenThoughAnIndexExists()
    {
        var explain = PlanExplain("SELECT * FROM t WHERE promotion_flag = 0", BuildCatalog());
        Assert.DoesNotContain("IndexScan", explain);
        Assert.Contains("SeqScan", explain);
    }

    [Fact]
    public void ChoiceIsCorrect_RegardlessOfWhichIndexWasCreatedFirst()
    {
        // guards against any "first/last index wins" bug rather than an actual cost comparison
        var catalog = BuildCatalog();
        Assert.Contains("IndexScan", PlanExplain("SELECT * FROM t WHERE sku = 'SKU-1'", catalog));
        Assert.DoesNotContain("IndexScan", PlanExplain("SELECT * FROM t WHERE promotion_flag = 1", catalog));
    }

    [Fact]
    public void HashIndex_IsNeverChosenForARangePredicate()
    {
        var schema = new Schema(new[] { new Column("price", SqlType.Decimal) });
        var rows = Enumerable.Range(0, 500).Select(i => new Row(new[] { SqlValue.Decimal(i) })).ToArray();
        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Table("t", schema, rows));
        catalog.CreateIndex("t", "price", IndexKind.Hash); // only a hash index exists

        var explain = PlanExplain("SELECT * FROM t WHERE price > 10", catalog);

        Assert.DoesNotContain("IndexScan", explain); // hash can't do ranges, so it must fall back
        Assert.Contains("SeqScan", explain);
    }

    [Fact]
    public void QueryStillReturnsCorrectRows_WhenIndexScanIsChosen()
    {
        var catalog = BuildCatalog();
        var plan = Planner.Plan(Parser.ParseSelect("SELECT sku FROM t WHERE sku = 'SKU-500'"), catalog);

        var rows = plan.Execute().ToList();

        Assert.Single(rows);
        Assert.Equal("SKU-500", rows[0][0].AsText);
    }

    [Fact]
    public void CompoundWhere_UsesIndexForOneConjunct_AndResidualFilterForTheRest()
    {
        var catalog = BuildCatalog();
        var plan = Planner.Plan(
            Parser.ParseSelect("SELECT * FROM t WHERE sku = 'SKU-500' AND promotion_flag = 0"), catalog);

        Assert.Contains("IndexScan", plan.Explain(0));
        var rows = plan.Execute().ToList();
        Assert.Single(rows); // SKU-500 has promotion_flag = 500 % 2 = 0, so it must survive the residual filter
    }
}
