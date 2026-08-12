namespace Sift.Tests.Differential;

/// <summary>
/// The fuzz half of the test oracle story (PLAN.md §10): 500+ randomly generated queries run
/// against both engines every test run. A fixed seed keeps failures reproducible — if this ever
/// goes red, the seed and the printed query are enough to reproduce and minimize the failure by hand.
/// </summary>
[Collection("Differential")]
public class RandomFuzzTests
{
    private const int QueryCount = 500;
    private const int Seed = 20260901;

    private readonly DifferentialFixture _fixture;

    public RandomFuzzTests(DifferentialFixture fixture) => _fixture = fixture;

    [Fact]
    public void FiveHundredRandomQueries_MatchSqlite()
    {
        var rng = new Random(Seed);
        var generator = new RandomQueryGenerator(rng, _fixture.Sales, _fixture.Brands);

        var failures = new List<(string Sql, string Error)>();
        for (var i = 0; i < QueryCount; i++)
        {
            var sql = generator.Next();
            try
            {
                _fixture.AssertEquivalent(sql);
            }
            catch (Exception ex)
            {
                failures.Add((sql, ex.Message));
                if (failures.Count >= 10) break; // report a sample, not a wall of near-duplicate failures
            }
        }

        if (failures.Count > 0)
        {
            var report = string.Join("\n---\n", failures.Select(f => $"{f.Sql}\n{f.Error}"));
            Assert.Fail($"{failures.Count} of {QueryCount} random queries diverged from SQLite:\n{report}");
        }
    }
}
