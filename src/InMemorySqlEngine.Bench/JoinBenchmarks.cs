using BenchmarkDotNet.Attributes;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Execution;

namespace InMemorySqlEngine.Bench;

/// <summary>Nested-loop vs hash join, fmcg_sales (190,757 rows) joined to brands (14 rows) on `brand`.</summary>
[ShortRunJob]
[MemoryDiagnoser]
public class JoinBenchmarks
{
    private Table _sales = null!;
    private Table _brands = null!;
    private int _salesJoinColumn;
    private int _brandsJoinColumn;

    [GlobalSetup]
    public void Setup()
    {
        _sales = CsvLoader.Load(BenchmarkData.FmcgSalesCsv, "fmcg_sales");
        _brands = CsvLoader.Load(BenchmarkData.BrandsCsv, "brands");
        _salesJoinColumn = _sales.Schema.IndexOf("brand");
        _brandsJoinColumn = _brands.Schema.IndexOf("brand");
    }

    [Benchmark(Baseline = true)]
    public int NestedLoop()
    {
        var join = new NestedLoopJoin(new SeqScan(_sales), new SeqScan(_brands), _salesJoinColumn, _brandsJoinColumn);
        var count = 0;
        foreach (var _ in join.Execute()) count++;
        return count;
    }

    [Benchmark]
    public int Hash()
    {
        var join = new HashJoin(new SeqScan(_sales), new SeqScan(_brands), _salesJoinColumn, _brandsJoinColumn);
        var count = 0;
        foreach (var _ in join.Execute()) count++;
        return count;
    }
}
