using System.Globalization;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Catalog;

/// <summary>Loads a CSV into an in-memory <see cref="Table"/>, inferring a column type from its values.</summary>
public static class CsvLoader
{
    public static Table Load(string path, string tableName)
    {
        using var reader = new StreamReader(path);

        var header = reader.ReadLine() ?? throw new InvalidOperationException($"'{path}' is empty");
        var columnNames = SplitLine(header);

        var rawRows = new List<string[]>();
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            if (line.Length == 0) continue;
            rawRows.Add(SplitLine(line));
        }

        var columnTypes = InferColumnTypes(columnNames.Length, rawRows);
        var schema = new Schema(columnNames.Select((name, i) => new Column(name, columnTypes[i])).ToArray());

        var rows = new Row[rawRows.Count];
        for (var r = 0; r < rawRows.Count; r++)
        {
            var values = new SqlValue[columnNames.Length];
            for (var c = 0; c < columnNames.Length; c++)
                values[c] = ParseValue(rawRows[r][c], columnTypes[c]);
            rows[r] = new Row(values);
        }

        return new Table(tableName, schema, rows);
    }

    private static SqlType[] InferColumnTypes(int columnCount, List<string[]> rawRows)
    {
        var types = new SqlType[columnCount];
        for (var c = 0; c < columnCount; c++)
        {
            var candidate = SqlType.Int;
            var sawAnyValue = false;

            foreach (var row in rawRows)
            {
                var field = row[c];
                if (field.Length == 0) continue; // NULL — doesn't narrow the type
                sawAnyValue = true;

                while (!Fits(candidate, field))
                    candidate = Widen(candidate);
            }

            types[c] = sawAnyValue ? candidate : SqlType.Text;
        }
        return types;
    }

    /// <summary>Widening order when a value doesn't fit the current guess: Int → Decimal → Date → Bool → Text.</summary>
    private static SqlType Widen(SqlType type) => type switch
    {
        SqlType.Int => SqlType.Decimal,
        SqlType.Decimal => SqlType.Date,
        SqlType.Date => SqlType.Bool,
        SqlType.Bool => SqlType.Text,
        SqlType.Text => SqlType.Text,
        _ => SqlType.Text
    };

    private static bool Fits(SqlType type, string field) => type switch
    {
        SqlType.Int => long.TryParse(field, NumberStyles.Integer, CultureInfo.InvariantCulture, out _),
        SqlType.Decimal => decimal.TryParse(field, NumberStyles.Float, CultureInfo.InvariantCulture, out _),
        SqlType.Date => DateTime.TryParse(field, CultureInfo.InvariantCulture, DateTimeStyles.None, out _),
        SqlType.Bool => bool.TryParse(field, out _),
        SqlType.Text => true,
        _ => false
    };

    private static SqlValue ParseValue(string field, SqlType type)
    {
        if (field.Length == 0) return SqlValue.Null(type);
        return type switch
        {
            SqlType.Int => SqlValue.Int(long.Parse(field, CultureInfo.InvariantCulture)),
            SqlType.Decimal => SqlValue.Decimal(decimal.Parse(field, NumberStyles.Float, CultureInfo.InvariantCulture)),
            SqlType.Date => SqlValue.Date(DateTime.Parse(field, CultureInfo.InvariantCulture)),
            SqlType.Bool => SqlValue.Bool(bool.Parse(field)),
            SqlType.Text => SqlValue.Text(field),
            _ => throw new InvalidOperationException($"unreachable: {type}")
        };
    }

    /// <summary>Minimal RFC4180 splitter: handles quoted fields with embedded commas/quotes.</summary>
    private static string[] SplitLine(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (inQuotes)
            {
                if (ch == '"')
                {
                    if (i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
                    else inQuotes = false;
                }
                else current.Append(ch);
            }
            else
            {
                if (ch == '"') inQuotes = true;
                else if (ch == ',') { fields.Add(current.ToString()); current.Clear(); }
                else current.Append(ch);
            }
        }
        fields.Add(current.ToString());
        return fields.ToArray();
    }
}
