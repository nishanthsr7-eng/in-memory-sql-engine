using Sift.Core.Catalog;
using Sift.Core.Values;

namespace Sift.Core.Indexing;

/// <summary>Exact-match only — O(1) lookup, no ordering, so no range scans (PLAN.md §6).</summary>
public sealed class HashIndex : IIndex
{
    private readonly Dictionary<SqlValue, List<int>> _buckets = new();

    public string ColumnName { get; }
    public IndexKind Kind => IndexKind.Hash;
    public bool SupportsRange => false;

    public HashIndex(string columnName, Table table)
    {
        ColumnName = columnName;
        var columnIndex = table.Schema.IndexOf(columnName);

        for (var rowId = 0; rowId < table.RowCount; rowId++)
        {
            var key = table.GetRow(rowId)[columnIndex];
            if (key.IsNull) continue; // `col = NULL` is never TRUE, so NULLs are never a useful lookup key

            if (!_buckets.TryGetValue(key, out var bucket)) _buckets[key] = bucket = new List<int>();
            bucket.Add(rowId);
        }
    }

    public IEnumerable<int> Lookup(SqlValue key) =>
        _buckets.TryGetValue(key, out var bucket) ? bucket : Enumerable.Empty<int>();

    public IEnumerable<int> Range(SqlValue? lo, bool loInclusive, SqlValue? hi, bool hiInclusive) =>
        throw new NotSupportedException($"hash index on '{ColumnName}' doesn't support range scans");
}
