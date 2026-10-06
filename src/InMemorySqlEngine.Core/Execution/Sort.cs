using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Planning;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

public readonly record struct SortKey(int ColumnIndex, bool Descending);

/// <summary>
/// The one operator that can't stream: it must see every row before it can emit the first one.
/// Materializes its child fully, sorts, then yields — deferred until enumeration starts, same
/// as every other operator, so a Sort that's never pulled from still does no work.
/// </summary>
public sealed class Sort : Operator
{
    private readonly Operator _child;
    private readonly SortKey[] _keys;

    public Sort(Operator child, SortKey[] keys)
    {
        _child = child;
        _keys = keys;
        EstimatedRowCount = child.EstimatedRowCount;
        EstimatedCost = child.EstimatedCost + CostModel.SortCost(child.EstimatedRowCount);
    }

    public override Schema OutputSchema => _child.OutputSchema;

    public override IEnumerable<Row> Execute()
    {
        var rows = _child.Execute().ToList();
        rows.Sort(Compare);
        foreach (var row in rows) yield return row;
    }

    private int Compare(Row a, Row b)
    {
        foreach (var key in _keys)
        {
            var cmp = CompareValues(a[key.ColumnIndex], b[key.ColumnIndex], key.Descending);
            if (cmp != 0) return cmp;
        }
        return 0;
    }

    /// <summary>NULLs sort last regardless of ASC/DESC — a fixed, documented rule (docs/design.md §6).</summary>
    private static int CompareValues(SqlValue a, SqlValue b, bool descending)
    {
        var cmp = a.CompareTo(b);
        if (a.IsNull || b.IsNull) return cmp; // nulls-last invariant holds either direction
        return descending ? -cmp : cmp;
    }

    public override string Explain(int indent)
    {
        var desc = string.Join(", ", _keys.Select(k => $"{OutputSchema.Columns[k.ColumnIndex].Name}{(k.Descending ? " DESC" : "")}"));
        return $"{Ind(indent)}Sort ({desc}){CostSuffix()}\n{_child.Explain(indent + 1)}";
    }
}
