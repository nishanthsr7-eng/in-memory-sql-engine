using BenchmarkDotNet.Attributes;
using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Bench;

/// <summary>
/// The speedup curve PLAN.md Phase 5 asks for: `SELECT brand, SUM(units_sold) FROM fmcg_sales
/// GROUP BY brand` (190,757 rows, 14 groups) at 1/2/4/8 threads. Also runs the sequential
/// `HashAggregate` as a fixed baseline, since "DegreeOfParallelism=1" on the parallel path still
/// pays partitioning/merge overhead a genuinely single-threaded implementation doesn't.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class ParallelAggregateBenchmarks
{
    private Table _table = null!;
    private AggregateSpec[] _specs = null!;
    private int[] _groupByIndices = null!;

    [Params(1, 2, 4, 8)]
    public int Threads;

    [GlobalSetup]
    public void Setup()
    {
        _table = CsvLoader.Load(BenchmarkData.FmcgSalesCsv, "fmcg_sales");
        _groupByIndices = new[] { _table.Schema.IndexOf("brand") };
        _specs = new[]
        {
            new AggregateSpec(AggregateFunc.Sum, _table.Schema.IndexOf("units_sold"), IsCountStar: false, "SUM(units_sold)", SqlType.Decimal),
        };
    }

    [Benchmark(Baseline = true)]
    public int SequentialHashAggregate()
    {
        var op = new HashAggregate(new SeqScan(_table), _groupByIndices, _specs);
        var count = 0;
        foreach (var _ in op.Execute()) count++;
        return count;
    }

    [Benchmark]
    public int ParallelHashAggregate()
    {
        var op = new Sift.Core.Execution.ParallelHashAggregate(_table, filter: null, _groupByIndices, _specs, Threads);
        var count = 0;
        foreach (var _ in op.Execute()) count++;
        return count;
    }
}
