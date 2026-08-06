using Sift.Core.Indexing;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Indexing;

public class BPlusTreeIndexTests
{
    [Fact]
    public void LookupFindsExactMatches_AndMissesReturnEmpty()
    {
        var index = new BPlusTreeIndex("x", order: 4);
        for (var i = 0; i < 20; i++) index.Insert(SqlValue.Int(i), rowId: i);

        Assert.Equal(new[] { 7 }, index.Lookup(SqlValue.Int(7)));
        Assert.Empty(index.Lookup(SqlValue.Int(999)));
    }

    [Fact]
    public void DuplicateKeys_AccumulateIntoOnePostingList()
    {
        var index = new BPlusTreeIndex("x", order: 4);
        index.Insert(SqlValue.Text("A"), 1);
        index.Insert(SqlValue.Text("A"), 2);
        index.Insert(SqlValue.Text("A"), 3);

        Assert.Equal(new[] { 1, 2, 3 }, index.Lookup(SqlValue.Text("A")).OrderBy(x => x));
    }

    [Fact]
    public void RangeScan_RespectsInclusivityOnBothBounds()
    {
        var index = new BPlusTreeIndex("x", order: 4);
        for (var i = 0; i < 10; i++) index.Insert(SqlValue.Int(i), i);

        Assert.Equal(new[] { 3, 4, 5 }, index.Range(SqlValue.Int(3), true, SqlValue.Int(5), true).OrderBy(x => x));
        Assert.Equal(new[] { 4 }, index.Range(SqlValue.Int(3), false, SqlValue.Int(5), false).OrderBy(x => x));
        Assert.Equal(new[] { 0, 1, 2 }, index.Range(null, false, SqlValue.Int(2), true).OrderBy(x => x));
        Assert.Equal(new[] { 8, 9 }, index.Range(SqlValue.Int(8), true, null, false).OrderBy(x => x));
    }

    [Fact]
    public void RangeScan_SpansMultipleLeavesViaLinkedList()
    {
        var index = new BPlusTreeIndex("x", order: 4); // tiny order forces many splits over 500 keys
        for (var i = 0; i < 500; i++) index.Insert(SqlValue.Int(i), i);

        var all = index.Range(null, false, null, false).OrderBy(x => x).ToList();
        Assert.Equal(Enumerable.Range(0, 500), all);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(64)]
    public void PropertyTest_AfterManyRandomInserts_TreeInvariantsHold(int order)
    {
        var index = new BPlusTreeIndex("x", order);
        var rng = new Random(42);
        var expected = new Dictionary<int, List<int>>();

        for (var i = 0; i < 2000; i++)
        {
            var key = rng.Next(0, 500); // guarantees duplicate keys too
            index.Insert(SqlValue.Int(key), i);
            if (!expected.TryGetValue(key, out var list)) expected[key] = list = new List<int>();
            list.Add(i);
        }

        // Invariant 1: every leaf is at the same depth.
        var depths = index.LeafDepths().Distinct().ToList();
        Assert.Single(depths);

        // Invariant 1b: every leaf holds between order/2 and order keys (root leaf would be
        // exempt from the lower bound, but 2000 inserts at these orders always force a split).
        foreach (var count in index.LeafKeyCounts())
        {
            Assert.True(count <= order, $"leaf key count {count} exceeds order {order}");
            Assert.True(count >= (order + 1) / 2, $"leaf key count {count} below minimum for order {order}");
        }

        // Invariant 2: every internal node's fan-out is within [order/2, order] children
        // (root is exempt from the lower bound — a B+ tree root may be sparse).
        var internalNodes = index.InternalNodes().ToList();
        Assert.NotEmpty(internalNodes); // 2000 inserts at these orders must produce at least one split
        var root = internalNodes[0];
        foreach (var (keys, childCount) in internalNodes)
        {
            Assert.True(childCount <= order, $"fan-out {childCount} exceeds order {order}");
            Assert.Equal(keys.Count + 1, childCount);
            if (!ReferenceEquals(keys, root.Keys))
                Assert.True(childCount >= (order + 1) / 2, $"fan-out {childCount} below minimum for order {order}");
        }

        // Invariant 3: keys are globally sorted (a full range scan proves it).
        var scanned = index.Range(null, false, null, false).ToList();
        Assert.True(scanned.Count > 0);

        // Invariant 4: every inserted (key, rowId) pair is found by Lookup, and nothing extra is.
        foreach (var (key, rowIds) in expected)
            Assert.Equal(rowIds.OrderBy(x => x), index.Lookup(SqlValue.Int(key)).OrderBy(x => x));
    }
}
