using System.Globalization;
using Microsoft.Data.Sqlite;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Planning;
using InMemorySqlEngine.Core.Sql;
using InMemorySqlEngine.Core.Values;
using Xunit;

namespace InMemorySqlEngine.Tests.Differential;

/// <summary>
/// Loads the same CSVs into the engine's Catalog and an in-memory SQLite database once per test run,
/// then runs the same SQL text against both — SQLite is a test oracle here, never a dependency
/// of the engine itself (docs/design.md §7).
/// </summary>
public sealed class DifferentialFixture : IDisposable
{
    public Catalog Catalog { get; } = new();
    public Table Sales { get; }
    public Table Brands { get; }

    private readonly SqliteConnection _connection;

    public DifferentialFixture()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();

        var repoRoot = FindRepoRoot();
        Sales = LoadIntoBoth(Path.Combine(repoRoot, "data", "fmcg_sales.csv"), "fmcg_sales");
        Brands = LoadIntoBoth(Path.Combine(repoRoot, "data", "brands.csv"), "brands");
    }

    public void Dispose() => _connection.Dispose();

    /// <summary>Runs the same SQL against the engine and SQLite; asserts they return the same rows,
    /// as sets (row order is never required to match — LIMIT/no-ORDER-BY makes it undefined
    /// anyway, and comparing as strings would make column-order-only differences fail spuriously).</summary>
    public void AssertEquivalent(string sql)
    {
        var engineRows = RunEngine(sql);
        var sqliteRows = RunSqlite(sql);

        var engineCanonical = engineRows.Select(CanonicalizeRow).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var sqliteCanonical = sqliteRows.Select(CanonicalizeRow).OrderBy(x => x, StringComparer.Ordinal).ToList();

        if (engineCanonical.Count != sqliteCanonical.Count || !engineCanonical.SequenceEqual(sqliteCanonical))
        {
            var onlyInEngine = engineCanonical.Except(sqliteCanonical).Take(5);
            var onlyInSqlite = sqliteCanonical.Except(engineCanonical).Take(5);
            Assert.Fail(
                $"query diverged: {sql}\n" +
                $"Engine {engineCanonical.Count} rows, SQLite {sqliteCanonical.Count} rows\n" +
                $"only in engine: {string.Join(" | ", onlyInEngine)}\n" +
                $"only in SQLite: {string.Join(" | ", onlyInSqlite)}");
        }
    }

    private List<string[]> RunEngine(string sql)
    {
        var plan = Planner.Plan(Parser.ParseSelect(sql), Catalog);
        return plan.Execute().Select(row => row.Values.Select(v => v.ToString()).ToArray()).ToList();
    }

    private List<string[]> RunSqlite(string sql)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = sql;
        using var reader = cmd.ExecuteReader();

        var results = new List<string[]>();
        while (reader.Read())
        {
            var row = new string[reader.FieldCount];
            for (var i = 0; i < reader.FieldCount; i++)
                row[i] = reader.IsDBNull(i) ? "NULL" : reader.GetValue(i).ToString() ?? "";
            results.Add(row);
        }
        return results;
    }

    private static readonly string CellSeparator = ((char)1).ToString();

    private static string CanonicalizeRow(string[] row) => string.Join(CellSeparator, row.Select(CanonicalizeCell));

    /// <summary>
    /// Both sides format cells slightly differently even when they agree: SQLite reports its
    /// own booleans as 0/1 where the engine prints "true"/"false", and SUM/AVG runs through SQLite's
    /// double-precision arithmetic against the engine's decimal accumulator — mathematically the same
    /// answer, not necessarily the same bit pattern. Rounding every numeric-looking cell absorbs
    /// that without masking a real divergence.
    /// </summary>
    private static string CanonicalizeCell(string text) => text switch
    {
        "NULL" => "NULL",
        "true" => "1",
        "false" => "0",
        // "0.######" (not plain ToString) matters: decimal.ToString() preserves whatever scale
        // the value happens to carry (25446.40m and 25446.4m print differently despite being
        // equal), which is exactly the kind of non-divergence this canonicalization exists to
        // absorb. The trailing-# format always trims to the same string regardless of source scale.
        _ when decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var dec) =>
            Math.Round(dec, 6, MidpointRounding.AwayFromZero).ToString("0.######", CultureInfo.InvariantCulture),
        _ => text
    };

    private static readonly Dictionary<SqlType, string> SqliteColumnType = new()
    {
        [SqlType.Int] = "INTEGER",
        // REAL, not TEXT — audit finding: a TEXT-affinity column compares against a numeric
        // literal by converting the *literal* to text (not the column to numeric), so `price_unit
        // BETWEEN -1.5 AND 10.25` on a TEXT column silently became a lexicographic string
        // comparison ("9.0" > "10.25" as strings, since '9' > '1'), undercounting matches by two
        // orders of magnitude — a bug in this harness, not in the engine, caught only because a plain
        // COUNT(*) (no LIMIT/ORDER BY ambiguity possible) made a wrong answer impossible to miss.
        // REAL trades exact decimal storage for correct numeric comparison; CanonicalizeCell's
        // rounding already absorbs the resulting float-vs-decimal formatting differences.
        [SqlType.Decimal] = "REAL",
        [SqlType.Text] = "TEXT",
        [SqlType.Date] = "TEXT",
        [SqlType.Bool] = "INTEGER",
    };

    private Table LoadIntoBoth(string csvPath, string tableName)
    {
        var table = CsvLoader.Load(csvPath, tableName);
        Catalog.AddTable(table);

        var columnDefs = string.Join(", ", table.Schema.Columns.Select(c => $"\"{c.Name}\" {SqliteColumnType[c.Type]}"));
        using (var create = _connection.CreateCommand())
        {
            create.CommandText = $"CREATE TABLE \"{tableName}\" ({columnDefs})";
            create.ExecuteNonQuery();
        }

        using var tx = _connection.BeginTransaction();
        using var insert = _connection.CreateCommand();
        insert.Transaction = tx;
        var paramNames = table.Schema.Columns.Select((_, i) => $"$p{i}").ToArray();
        insert.CommandText = $"INSERT INTO \"{tableName}\" VALUES ({string.Join(",", paramNames)})";
        var parameters = paramNames.Select(n => insert.Parameters.Add(new SqliteParameter(n, DBNull.Value))).ToArray();

        foreach (var row in table.Rows)
        {
            for (var i = 0; i < row.Count; i++) parameters[i].Value = ToSqliteParam(row[i]);
            insert.ExecuteNonQuery();
        }
        tx.Commit();

        return table;
    }

    private static object ToSqliteParam(SqlValue value)
    {
        if (value.IsNull) return DBNull.Value;
        return value.Type switch
        {
            SqlType.Int => value.AsInt,
            SqlType.Decimal => (double)value.AsDecimal,
            SqlType.Text => value.AsText,
            SqlType.Date => value.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SqlType.Bool => value.AsBool ? 1L : 0L,
            _ => throw new InvalidOperationException($"unreachable: {value.Type}")
        };
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "data")) && Directory.Exists(Path.Combine(dir.FullName, "src")))
                return dir.FullName;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("could not locate repo root (looked for sibling 'data' and 'src' directories)");
    }
}

[CollectionDefinition("Differential")]
public sealed class DifferentialCollection : ICollectionFixture<DifferentialFixture>;
