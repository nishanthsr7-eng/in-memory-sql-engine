namespace Sift.Core.Catalog;

/// <summary>An in-memory, row-oriented table. Row ids are stable positions into <see cref="Rows"/>.</summary>
public sealed class Table
{
    public string Name { get; }
    public Schema Schema { get; }
    public IReadOnlyList<Row> Rows { get; }
    public Statistics Statistics { get; }

    public Table(string name, Schema schema, IReadOnlyList<Row> rows)
    {
        Name = name;
        Schema = schema;
        Rows = rows;
        Statistics = Statistics.Collect(schema, rows);
    }

    public int RowCount => Rows.Count;

    public Row GetRow(int rowId) => Rows[rowId];
}
