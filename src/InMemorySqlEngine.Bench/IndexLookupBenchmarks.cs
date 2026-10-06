using BenchmarkDotNet.Attributes;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Indexing;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Bench;

/// <summary>B+ tree vs hash index on exact-match lookup, on the real fmcg_sales `sku` column (30 distinct values, 190,757 rows).</summary>
[ShortRunJob]
[MemoryDiagnoser]
public class IndexLookupBenchmarks
{
    private BPlusTreeIndex _btree = null!;
    private HashIndex _hash = null!;

    [GlobalSetup]
    public void Setup()
    {
        var table = CsvLoader.Load(BenchmarkData.FmcgSalesCsv, "fmcg_sales");
        _btree = new BPlusTreeIndex("sku", table);
        _hash = new HashIndex("sku", table);
    }

    [Benchmark(Baseline = true)]
    public int HashLookup()
    {
        var count = 0;
        foreach (var _ in _hash.Lookup(SqlValue.Text("MI-006"))) count++;
        return count;
    }

    [Benchmark]
    public int BTreeLookup()
    {
        var count = 0;
        foreach (var _ in _btree.Lookup(SqlValue.Text("MI-006"))) count++;
        return count;
    }
}
