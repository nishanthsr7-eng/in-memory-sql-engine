using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Catalog;

/// <summary>
/// A tuple of values. Deliberately schema-less — the schema lives on the operator that
/// produced the row (Operator.OutputSchema), not on each row, so rows stay cheap to pass around.
/// </summary>
public readonly struct Row
{
    public SqlValue[] Values { get; }

    public Row(SqlValue[] values) => Values = values;

    public SqlValue this[int ordinal] => Values[ordinal];

    public int Count => Values.Length;
}
