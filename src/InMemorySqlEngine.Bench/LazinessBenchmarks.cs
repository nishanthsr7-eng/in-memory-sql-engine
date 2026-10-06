using BenchmarkDotNet.Attributes;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Execution;

namespace InMemorySqlEngine.Bench;

/// <summary>`LIMIT 10` pulled lazily through the operator tree vs materializing the full 190,757-row scan first.</summary>
[ShortRunJob]
[MemoryDiagnoser]
public class LazinessBenchmarks
{
    private Table _table = null!;

    [GlobalSetup]
    public void Setup() => _table = CsvLoader.Load(BenchmarkData.FmcgSalesCsv, "fmcg_sales");

    [Benchmark(Baseline = true)]
    public int MaterializeThenTake()
    {
        var all = new SeqScan(_table).Execute().ToList(); // forces every row to be enumerated up front
        return all.Take(10).Count();
    }

    [Benchmark]
    public int LazyLimit()
    {
        var plan = new Limit(new SeqScan(_table), 10);
        var count = 0;
        foreach (var _ in plan.Execute()) count++;
        return count;
    }
}
