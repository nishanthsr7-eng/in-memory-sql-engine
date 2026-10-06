namespace InMemorySqlEngine.Core.Catalog;

public sealed class Schema
{
    private readonly Dictionary<string, int> _indexByName;

    public IReadOnlyList<Column> Columns { get; }

    public Schema(IReadOnlyList<Column> columns)
    {
        Columns = columns;
        _indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < columns.Count; i++)
            _indexByName[columns[i].Name] = i;
    }

    public int IndexOf(string columnName)
    {
        if (_indexByName.TryGetValue(columnName, out var index)) return index;
        throw new ArgumentException($"unknown column '{columnName}'");
    }

    public bool TryIndexOf(string columnName, out int index) => _indexByName.TryGetValue(columnName, out index);

    public Column Get(string columnName) => Columns[IndexOf(columnName)];

    /// <summary>Builds a schema for a subset of columns, in the given order — used by Project.</summary>
    public Schema Subset(IReadOnlyList<string> columnNames)
    {
        var columns = new Column[columnNames.Count];
        for (var i = 0; i < columnNames.Count; i++)
            columns[i] = Get(columnNames[i]);
        return new Schema(columns);
    }
}
