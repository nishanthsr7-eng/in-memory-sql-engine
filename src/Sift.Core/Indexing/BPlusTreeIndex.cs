using Sift.Core.Catalog;
using Sift.Core.Values;

namespace Sift.Core.Indexing;

/// <summary>
/// A B+ tree secondary index: unique keys route through internal nodes, each leaf key holds a
/// posting list of matching row ids, and leaves are linked for sequential range scans. Insert
/// and search only — deletion rebalancing is out of scope for a read-only engine (docs/design.md §1).
///
/// Node splitting is hand-written, not delegated to <c>SortedDictionary</c>: fan-out (order) is
/// tunable, and leaf linking — the thing that makes range scans fast — isn't something the BCL
/// type gives you.
/// </summary>
public sealed class BPlusTreeIndex : IIndex
{
    public const int DefaultOrder = 64;

    private readonly int _maxKeys; // a node splits once it holds more than this many keys
    private BPlusTreeNode _root;

    public string ColumnName { get; }
    public IndexKind Kind => IndexKind.BPlusTree;
    public bool SupportsRange => true;

    public BPlusTreeIndex(string columnName, int order = DefaultOrder)
    {
        if (order < 3) throw new ArgumentOutOfRangeException(nameof(order), "order must be at least 3");
        ColumnName = columnName;
        _maxKeys = order - 1;
        _root = new BPlusTreeLeaf();
    }

    public BPlusTreeIndex(string columnName, Table table, int order = DefaultOrder) : this(columnName, order)
    {
        var columnIndex = table.Schema.IndexOf(columnName);
        for (var rowId = 0; rowId < table.RowCount; rowId++)
        {
            var key = table.GetRow(rowId)[columnIndex];
            if (!key.IsNull) Insert(key, rowId);
        }
    }

    public void Insert(SqlValue key, int rowId)
    {
        var split = InsertRecursive(_root, key, rowId);
        if (split is not { } s) return;

        var newRoot = new BPlusTreeInternal();
        newRoot.Keys.Add(s.SplitKey);
        newRoot.Children.Add(_root);
        newRoot.Children.Add(s.NewSibling);
        _root = newRoot;
    }

    public IEnumerable<int> Lookup(SqlValue key)
    {
        var leaf = FindLeafForKey(key);
        var idx = LowerBound(leaf.Keys, key);
        return idx < leaf.Keys.Count && leaf.Keys[idx].Equals(key) ? leaf.Values[idx] : Enumerable.Empty<int>();
    }

    public IEnumerable<int> Range(SqlValue? lo, bool loInclusive, SqlValue? hi, bool hiInclusive)
    {
        var leaf = lo is { } loKey ? FindLeafForKey(loKey) : FindLeftmostLeaf();
        var startIndex = lo is { } lk ? LowerBound(leaf.Keys, lk) : 0;

        for (var current = leaf; current is not null; current = current.Next)
        {
            for (var i = current == leaf ? startIndex : 0; i < current.Keys.Count; i++)
            {
                var key = current.Keys[i];

                if (lo is { } loBound && !loInclusive && key.Equals(loBound)) continue;
                if (hi is { } hiBound)
                {
                    var cmp = key.CompareTo(hiBound);
                    if (cmp > 0 || (cmp == 0 && !hiInclusive)) yield break;
                }

                foreach (var rowId in current.Values[i]) yield return rowId;
            }
        }
    }

    private readonly record struct SplitResult(SqlValue SplitKey, BPlusTreeNode NewSibling);

    private SplitResult? InsertRecursive(BPlusTreeNode node, SqlValue key, int rowId)
    {
        if (node is BPlusTreeLeaf leaf) return InsertIntoLeaf(leaf, key, rowId);

        var internalNode = (BPlusTreeInternal)node;
        var childIndex = UpperBound(internalNode.Keys, key);
        var childSplit = InsertRecursive(internalNode.Children[childIndex], key, rowId);
        if (childSplit is not { } split) return null;

        internalNode.Keys.Insert(childIndex, split.SplitKey);
        internalNode.Children.Insert(childIndex + 1, split.NewSibling);

        return internalNode.Keys.Count <= _maxKeys ? null : SplitInternal(internalNode);
    }

    private SplitResult? InsertIntoLeaf(BPlusTreeLeaf leaf, SqlValue key, int rowId)
    {
        var idx = LowerBound(leaf.Keys, key);
        if (idx < leaf.Keys.Count && leaf.Keys[idx].Equals(key))
        {
            leaf.Values[idx].Add(rowId);
            return null;
        }

        leaf.Keys.Insert(idx, key);
        leaf.Values.Insert(idx, new List<int> { rowId });

        return leaf.Keys.Count <= _maxKeys ? null : SplitLeaf(leaf);
    }

    /// <summary>Leaf split: the right half moves to a new leaf; its first key is *copied* up as the separator (it still lives in the leaf).</summary>
    private static SplitResult SplitLeaf(BPlusTreeLeaf leaf)
    {
        var mid = leaf.Keys.Count / 2;
        var right = new BPlusTreeLeaf();

        right.Keys.AddRange(leaf.Keys.GetRange(mid, leaf.Keys.Count - mid));
        right.Values.AddRange(leaf.Values.GetRange(mid, leaf.Values.Count - mid));
        leaf.Keys.RemoveRange(mid, leaf.Keys.Count - mid);
        leaf.Values.RemoveRange(mid, leaf.Values.Count - mid);

        right.Next = leaf.Next;
        leaf.Next = right;

        return new SplitResult(right.Keys[0], right);
    }

    /// <summary>Internal split: the middle key moves up (it doesn't stay in either half — internal nodes hold no data, only routing).</summary>
    private static SplitResult SplitInternal(BPlusTreeInternal node)
    {
        var mid = node.Keys.Count / 2;
        var promoted = node.Keys[mid];
        var right = new BPlusTreeInternal();

        right.Keys.AddRange(node.Keys.GetRange(mid + 1, node.Keys.Count - mid - 1));
        right.Children.AddRange(node.Children.GetRange(mid + 1, node.Children.Count - mid - 1));

        node.Keys.RemoveRange(mid, node.Keys.Count - mid);
        node.Children.RemoveRange(mid + 1, node.Children.Count - mid - 1);

        return new SplitResult(promoted, right);
    }

    private BPlusTreeLeaf FindLeafForKey(SqlValue key)
    {
        var node = _root;
        while (node is BPlusTreeInternal internalNode)
            node = internalNode.Children[UpperBound(internalNode.Keys, key)];
        return (BPlusTreeLeaf)node;
    }

    private BPlusTreeLeaf FindLeftmostLeaf()
    {
        var node = _root;
        while (node is BPlusTreeInternal internalNode) node = internalNode.Children[0];
        return (BPlusTreeLeaf)node;
    }

    /// <summary>First index whose key is &gt;= <paramref name="key"/> (leaf search / insertion point).</summary>
    private static int LowerBound(List<SqlValue> keys, SqlValue key)
    {
        int lo = 0, hi = keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (keys[mid].CompareTo(key) < 0) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>First index whose key is &gt; <paramref name="key"/> — i.e. the child that owns <paramref name="key"/>.</summary>
    private static int UpperBound(List<SqlValue> keys, SqlValue key)
    {
        int lo = 0, hi = keys.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (key.CompareTo(keys[mid]) < 0) hi = mid; else lo = mid + 1;
        }
        return lo;
    }

    // --- test-only introspection (property tests verify real B+ tree invariants) ---
    internal int Height()
    {
        var height = 1;
        var node = _root;
        while (node is BPlusTreeInternal internalNode) { height++; node = internalNode.Children[0]; }
        return height;
    }

    internal IEnumerable<int> LeafDepths()
    {
        IEnumerable<int> Walk(BPlusTreeNode node, int depth) => node switch
        {
            BPlusTreeLeaf => new[] { depth },
            BPlusTreeInternal i => i.Children.SelectMany(c => Walk(c, depth + 1)),
            _ => throw new InvalidOperationException()
        };
        return Walk(_root, 1);
    }

    internal IEnumerable<int> LeafKeyCounts()
    {
        IEnumerable<int> Walk(BPlusTreeNode node) => node switch
        {
            BPlusTreeLeaf leaf => new[] { leaf.Keys.Count },
            BPlusTreeInternal i => i.Children.SelectMany(Walk),
            _ => throw new InvalidOperationException()
        };
        return Walk(_root);
    }

    internal IEnumerable<(List<SqlValue> Keys, int ChildCount)> InternalNodes()
    {
        IEnumerable<(List<SqlValue>, int)> Walk(BPlusTreeNode node)
        {
            if (node is not BPlusTreeInternal i) yield break;
            yield return (i.Keys, i.Children.Count);
            foreach (var child in i.Children)
                foreach (var entry in Walk(child)) yield return entry;
        }
        return Walk(_root);
    }
}
