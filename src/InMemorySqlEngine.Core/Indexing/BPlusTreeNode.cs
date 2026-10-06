using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Indexing;

internal abstract class BPlusTreeNode
{
    public readonly List<SqlValue> Keys = new();
}

/// <summary>
/// Holds the actual (key → row ids) entries. Leaves are singly linked (<see cref="Next"/>) so a
/// range scan, once it finds the starting leaf, walks forward without ever touching the tree
/// above it again — the whole point of a B+ tree over a plain sorted array.
/// </summary>
internal sealed class BPlusTreeLeaf : BPlusTreeNode
{
    public readonly List<List<int>> Values = new();
    public BPlusTreeLeaf? Next;
}

/// <summary>
/// Routing only — no data. <c>Children.Count == Keys.Count + 1</c>; <c>Children[i]</c> covers
/// keys in <c>[Keys[i-1], Keys[i])</c> (Children[0] covers everything below Keys[0]).
/// </summary>
internal sealed class BPlusTreeInternal : BPlusTreeNode
{
    public readonly List<BPlusTreeNode> Children = new();
}
