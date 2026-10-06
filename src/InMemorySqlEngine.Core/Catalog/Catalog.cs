using InMemorySqlEngine.Core.Indexing;

namespace InMemorySqlEngine.Core.Catalog;

/// <summary>Registry of loaded tables and their indexes, keyed case-insensitively by name.</summary>
public sealed class Catalog
{
    private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string Table, string Column), IIndex> _indexes = new();

    public IEnumerable<string> TableNames => _tables.Values.Select(t => t.Name);

    public void AddTable(Table table) => _tables[table.Name] = table;

    public Table GetTable(string name)
    {
        if (_tables.TryGetValue(name, out var table)) return table;
        throw new ArgumentException($"unknown table '{name}'");
    }

    public bool TryGetTable(string name, out Table table) => _tables.TryGetValue(name, out table!);

    public IIndex CreateIndex(string tableName, string columnName, IndexKind kind)
    {
        var table = GetTable(tableName);
        var canonicalColumn = table.Schema.Get(columnName).Name;

        IIndex index = kind switch
        {
            IndexKind.Hash => new HashIndex(canonicalColumn, table),
            IndexKind.BPlusTree => new BPlusTreeIndex(canonicalColumn, table),
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
        };

        _indexes[(table.Name, canonicalColumn)] = index;
        return index;
    }

    public bool TryGetIndex(string tableName, string columnName, out IIndex index)
    {
        if (_tables.TryGetValue(tableName, out var table) &&
            table.Schema.TryIndexOf(columnName, out var ordinal) &&
            _indexes.TryGetValue((table.Name, table.Schema.Columns[ordinal].Name), out var found))
        {
            index = found;
            return true;
        }
        index = null!;
        return false;
    }
}
