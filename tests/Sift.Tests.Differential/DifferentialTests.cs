namespace Sift.Tests.Differential;

[Collection("Differential")]
public class DifferentialTests
{
    private readonly DifferentialFixture _fixture;

    public DifferentialTests(DifferentialFixture fixture) => _fixture = fixture;

    [Fact]
    public void SimpleSelect_MatchesSqlite()
    {
        _fixture.AssertEquivalent("SELECT brand, units_sold FROM fmcg_sales WHERE region = 'PL-North' LIMIT 50");
    }

    [Fact]
    public void GroupByAggregate_MatchesSqlite()
    {
        _fixture.AssertEquivalent("SELECT brand, COUNT(*), SUM(units_sold), AVG(price_unit) FROM fmcg_sales GROUP BY brand");
    }

    [Fact]
    public void Join_MatchesSqlite()
    {
        // NB: "category" exists on both sides of this join (fmcg_sales and brands) and must be
        // qualified — SQLite rejects the unqualified form as ambiguous, which is what caught
        // that Sift's own combined post-join schema doesn't (yet) reject it the same way; see
        // README "Known limitations".
        _fixture.AssertEquivalent(
            "SELECT s.brand, b.category FROM fmcg_sales s JOIN brands b ON s.brand = b.brand WHERE promotion_flag = 1");
    }

    /// <summary>The exact bug class the plan calls out (PLAN.md §1, §10): `!=` against a NULL-bearing
    /// column must exclude NULL rows (three-valued UNKNOWN), which a naive engine gets wrong.</summary>
    [Fact]
    public void NotEqualAgainstNullableColumn_ExcludesNulls_MatchingSqliteSemantics()
    {
        _fixture.AssertEquivalent("SELECT sku FROM fmcg_sales WHERE stock_available != 0");
    }

    [Fact]
    public void IsNullAndIsNotNull_MatchSqlite()
    {
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE delivery_days IS NULL");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE delivery_days IS NOT NULL");
    }

    [Fact]
    public void Between_MatchesSqlite()
    {
        _fixture.AssertEquivalent("SELECT sku FROM fmcg_sales WHERE price_unit BETWEEN 1.5 AND 2.5");
    }

    [Fact]
    public void HavingAndOrderByAndLimit_MatchSqlite()
    {
        _fixture.AssertEquivalent(
            "SELECT region, COUNT(*) AS n FROM fmcg_sales GROUP BY region HAVING COUNT(*) > 1000 ORDER BY n DESC LIMIT 3");
    }
}
