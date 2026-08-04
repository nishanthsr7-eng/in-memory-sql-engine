namespace Sift.Core.Catalog;

/// <summary>Registry of loaded tables, keyed case-insensitively by name.</summary>
public sealed class Catalog
{
    private readonly Dictionary<string, Table> _tables = new(StringComparer.OrdinalIgnoreCase);

    public IEnumerable<string> TableNames => _tables.Values.Select(t => t.Name);

    public void AddTable(Table table) => _tables[table.Name] = table;

    public Table GetTable(string name)
    {
        if (_tables.TryGetValue(name, out var table)) return table;
        throw new ArgumentException($"unknown table '{name}'");
    }

    public bool TryGetTable(string name, out Table table) => _tables.TryGetValue(name, out table!);
}
