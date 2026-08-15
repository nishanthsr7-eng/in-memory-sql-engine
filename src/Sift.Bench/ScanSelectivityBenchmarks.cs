using BenchmarkDotNet.Attributes;
using Sift.Core.Catalog;
using Sift.Core.Indexing;
using Sift.Core.Values;

namespace Sift.Bench;

/// <summary>
/// The headline benchmark (PLAN.md §9): SeqScan vs IndexScan cost as a function of selectivity.
/// A synthetic table with a controllable number of distinct "bucket" values stands in for a
/// real column, so selectivity (1/DistinctValues) can be swept across four orders of magnitude
/// on the same 190k-row shape as fmcg_sales — real timings, not the CostModel's estimates.
/// </summary>
[ShortRunJob]
[MemoryDiagnoser]
public class ScanSelectivityBenchmarks
{
    private const int RowCount = 190_000;

    [Params(1, 10, 100, 1000)] // selectivity = 1/DistinctValues: 1.0, 0.1, 0.01, 0.001
    public int DistinctValues;

    private Table _table = null!;
    private BPlusTreeIndex _index = null!;

    [GlobalSetup]
    public void Setup()
    {
        var schema = new Schema(new[] { new Column("bucket", SqlType.Int) });
        var rows = new Row[RowCount];
        var rng = new Random(42);
        for (var i = 0; i < RowCount; i++) rows[i] = new Row(new[] { SqlValue.Int(rng.Next(DistinctValues)) });

        _table = new Table("bench", schema, rows);
        _index = new BPlusTreeIndex("bucket", _table);
    }

    [Benchmark(Baseline = true)]
    public int SeqScan()
    {
        var count = 0;
        foreach (var row in _table.Rows)
            if (row[0].AsInt == 0) count++;
        return count;
    }

    [Benchmark]
    public int IndexScan()
    {
        var count = 0;
        foreach (var _ in _index.Lookup(SqlValue.Int(0))) count++;
        return count;
    }
}
