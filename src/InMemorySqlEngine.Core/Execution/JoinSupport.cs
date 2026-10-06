using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

/// <summary>Row/schema concatenation shared by <see cref="NestedLoopJoin"/> and <see cref="HashJoin"/>.</summary>
internal static class JoinSupport
{
    public static Row Combine(Row left, Row right)
    {
        var values = new SqlValue[left.Count + right.Count];
        Array.Copy(left.Values, values, left.Count);
        Array.Copy(right.Values, 0, values, left.Count, right.Count);
        return new Row(values);
    }

    public static Schema CombineSchemas(Schema left, Schema right)
    {
        var columns = new Column[left.Columns.Count + right.Columns.Count];
        for (var i = 0; i < left.Columns.Count; i++) columns[i] = left.Columns[i];
        for (var i = 0; i < right.Columns.Count; i++) columns[left.Columns.Count + i] = right.Columns[i];
        return new Schema(columns);
    }
}
