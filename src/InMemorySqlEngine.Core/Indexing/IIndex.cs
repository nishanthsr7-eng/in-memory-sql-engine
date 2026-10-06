using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Indexing;

public enum IndexKind { Hash, BPlusTree }

/// <summary>
/// A secondary index: column value → row ids. Deliberately capability-aware — a hash index
/// can't answer a range query, and the planner has to know that up front rather than build a
/// physical plan it can't execute (see <see cref="SupportsRange"/>, docs/design.md §4).
/// </summary>
public interface IIndex
{
    string ColumnName { get; }
    IndexKind Kind { get; }
    bool SupportsRange { get; }

    IEnumerable<int> Lookup(SqlValue key);

    /// <summary>
    /// Row ids with keys in [lo, hi] (bounds independently inclusive/exclusive, either end
    /// omittable for an open range). Throws <see cref="NotSupportedException"/> if <see cref="SupportsRange"/> is false.
    /// </summary>
    IEnumerable<int> Range(SqlValue? lo, bool loInclusive, SqlValue? hi, bool hiInclusive);
}
