using Sift.Core.Catalog;

namespace Sift.Core.Execution;

/// <summary>
/// A node in the physical plan: a pull-based (Volcano-style) iterator. `Execute()` returns an
/// `IEnumerable&lt;Row&gt;` built with `yield return` throughout the tree, so pulling one row from
/// the root only does as much work as that one row requires — memory stays flat regardless of
/// table size, and a `Limit` on top short-circuits every operator beneath it.
/// </summary>
public abstract class Operator
{
    public abstract Schema OutputSchema { get; }

    public abstract IEnumerable<Row> Execute();

    public abstract string Explain(int indent);

    protected static string Ind(int indent) => new(' ', indent * 2);
}
