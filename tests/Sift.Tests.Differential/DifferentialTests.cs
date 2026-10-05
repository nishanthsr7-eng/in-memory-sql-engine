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

    /// <summary>A bug class the oracle exists to catch (docs/design.md §7): `!=` against a NULL-bearing
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

    /// <summary>
    /// Regression test: every value in this dataset is non-negative, so nothing else in this
    /// file (or the random fuzzer, before it was fixed to occasionally negate a sampled literal)
    /// ever exercised the lexer's negative-number path. It was broken — `-5` failed to parse at
    /// all — until this audit caught it; see RandomQueryGenerator's `Negate` for the fuzzer fix.
    ///
    /// No LIMIT here deliberately: both predicates below match essentially every row (nothing in
    /// this dataset is actually negative), and this audit *also* found that `LIMIT n` without a
    /// fully-determining `ORDER BY` isn't safely comparable across engines in the first place —
    /// SQLite doesn't preserve insertion order under LIMIT the way an earlier version of this
    /// suite assumed. Comparing the full (still bounded, ~190k row) result sides-steps that
    /// entirely rather than re-introducing it here.
    /// </summary>
    [Fact]
    public void NegativeNumericLiterals_ParseAndMatchSqlite()
    {
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE units_sold > -5");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE price_unit BETWEEN -1.5 AND 10.25");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE stock_available != -1");
        _fixture.AssertEquivalent("SELECT sku FROM fmcg_sales WHERE units_sold = -1"); // no real row matches; must return empty on both sides
    }

    /// <summary>
    /// Regression test for a bug in this harness itself (not the engine), found by the query
    /// above: storing DECIMAL columns as SQLite TEXT makes range comparisons compare as strings,
    /// not numbers — "9.0" > "10.25" lexicographically, since '9' > '1'. real price_unit values
    /// (1.5–9.0) confirm the correct numeric answer is "every row matches"; the bug silently
    /// undercounted to ~12,564 of 190,757. Covers every comparison operator, not just BETWEEN.
    /// </summary>
    [Fact]
    public void DecimalColumn_RangeComparisons_AreNumericNotLexicographic()
    {
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE price_unit BETWEEN -1.5 AND 10.25");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE price_unit > 8.5");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE price_unit < 2.0");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE price_unit >= 9.0");
    }

    /// <summary>
    /// DATE is also stored as SQLite TEXT (unlike DECIMAL, not changed by the fix above) — safe
    /// specifically because ISO-8601 'yyyy-MM-dd' is fixed-width and zero-padded, so lexicographic
    /// string comparison and chronological comparison agree. Covers a comparison shape the random
    /// fuzzer never generates at all (it only tests `=`/`!=`/IS [NOT] NULL on DATE columns), so
    /// this is the only thing standing between "believed safe by reasoning" and "verified."
    /// </summary>
    [Fact]
    public void DateColumn_RangeComparisons_MatchSqlite()
    {
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE date > '2023-06-01'");
        _fixture.AssertEquivalent("SELECT COUNT(*) FROM fmcg_sales WHERE date BETWEEN '2022-01-01' AND '2022-12-31'");
    }
}
