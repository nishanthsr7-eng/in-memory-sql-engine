using BenchmarkDotNet.Attributes;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Indexing;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Bench;

/// <summary>B+ tree range query vs a full scan with the equivalent filter, on the real fmcg_sales `price_unit` column.</summary>
[ShortRunJob]
[MemoryDiagnoser]
public class RangeScanBenchmarks
{
    private static readonly SqlValue Lo = SqlValue.Decimal(2m);
    private static readonly SqlValue Hi = SqlValue.Decimal(3m);

    private Table _table = null!;
    private BPlusTreeIndex _index = null!;
    private int _priceColumnIndex;

    [GlobalSetup]
    public void Setup()
    {
        _table = CsvLoader.Load(BenchmarkData.FmcgSalesCsv, "fmcg_sales");
        _index = new BPlusTreeIndex("price_unit", _table);
        _priceColumnIndex = _table.Schema.IndexOf("price_unit");
    }

    [Benchmark(Baseline = true)]
    public int FullScan()
    {
        var count = 0;
        foreach (var row in _table.Rows)
        {
            var value = row[_priceColumnIndex];
            if (!value.IsNull && value.GreaterThanOrEqual(Lo).IsTrue() && value.LessThanOrEqual(Hi).IsTrue()) count++;
        }
        return count;
    }

    [Benchmark]
    public int RangeScan()
    {
        var count = 0;
        foreach (var _ in _index.Range(Lo, true, Hi, true)) count++;
        return count;
    }
}
