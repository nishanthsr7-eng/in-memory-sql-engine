using System.Globalization;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Tests.Differential;

/// <summary>
/// Generates random-but-valid SQL within the engine's own grammar (docs/design.md §1) against the real
/// fmcg_sales/brands tables. Literal values are sampled from actual data so predicates produce
/// real matches instead of mostly-empty results — an all-empty fuzz suite proves nothing.
/// </summary>
public sealed class RandomQueryGenerator
{
    private readonly Random _rng;
    private readonly Table _sales;
    private readonly Table _brands;
    private readonly Dictionary<string, List<SqlValue>> _salesSamples;
    private readonly Dictionary<string, List<SqlValue>> _brandsSamples;

    private static readonly string[] AggregateFuncs = { "COUNT", "SUM", "AVG", "MIN", "MAX" };

    public RandomQueryGenerator(Random rng, Table sales, Table brands)
    {
        _rng = rng;
        _sales = sales;
        _brands = brands;
        _salesSamples = SampleColumnValues(sales, rng);
        _brandsSamples = SampleColumnValues(brands, rng);
    }

    private static Dictionary<string, List<SqlValue>> SampleColumnValues(Table table, Random rng)
    {
        var result = new Dictionary<string, List<SqlValue>>();
        for (var c = 0; c < table.Schema.Columns.Count; c++)
        {
            var name = table.Schema.Columns[c].Name;
            var samples = new List<SqlValue>();
            for (var i = 0; i < 30; i++)
            {
                var row = table.Rows[rng.Next(table.RowCount)];
                if (!row[c].IsNull) samples.Add(row[c]);
            }
            result[name] = samples.Count > 0 ? samples : new List<SqlValue> { table.Rows[0][c] };
        }
        return result;
    }

    public string Next() => _rng.Next(10) switch
    {
        < 5 => SimpleSelect(),
        < 8 => GroupByAggregate(),
        _ => JoinQuery(),
    };

    private string SimpleSelect()
    {
        var sb = new System.Text.StringBuilder("SELECT ");
        sb.Append(RandomColumnList(_sales));
        sb.Append(" FROM fmcg_sales");

        // WHERE is mandatory here (not just likely) to bound comparison cost — without it, the
        // no-LIMIT policy below means every unfiltered case would compare all 190,757 rows.
        sb.Append(" WHERE ").Append(RandomPredicate(_sales, _salesSamples, qualifier: null));

        // No bare LIMIT here — audit finding: LIMIT without an ORDER BY that fully determines
        // row order is NOT safely differential-testable against a second engine at all, not just
        // in the tie-at-the-boundary case originally guarded against below. Proven empirically:
        // `WHERE price_unit BETWEEN -1.5 AND 10.25 LIMIT 10` (a predicate matching essentially
        // the whole table) returned the engine's literal first 10 CSV rows but a *different* 10 from
        // SQLite, which does not preserve insertion/rowid order under LIMIT the way this
        // generator used to assume. ORDER BY alone (no LIMIT) stays safe: AssertEquivalent
        // compares as a sorted set, so the *order* SQLite and the engine return it in never matters — only
        // that both compute the same full result, which a resort always does deterministically.
        if (_rng.Next(2) == 0)
            sb.Append(" ORDER BY ").Append(RandomColumnName(_sales)).Append(_rng.Next(2) == 0 ? " DESC" : " ASC");

        return sb.ToString();
    }

    private string GroupByAggregate()
    {
        var groupCol = RandomTextColumn(_sales);
        var measureCol = RandomNumericColumn(_sales);
        var func = AggregateFuncs[_rng.Next(AggregateFuncs.Length)];
        var aggExpr = func == "COUNT" && _rng.Next(2) == 0 ? "COUNT(*)" : $"{func}({measureCol})";

        var sb = new System.Text.StringBuilder($"SELECT {groupCol}, {aggExpr} AS agg_value FROM fmcg_sales");

        if (_rng.Next(3) != 0) sb.Append(" WHERE ").Append(RandomPredicate(_sales, _salesSamples, qualifier: null));
        sb.Append(" GROUP BY ").Append(groupCol);
        if (_rng.Next(3) == 0) sb.Append(" HAVING ").Append(aggExpr).Append(" > 0");

        // LIMIT here is only ever paired with ORDER BY on the GROUP BY column itself — it's
        // unique per output row (one row per group), unlike agg_value, which different groups
        // can easily tie on. Plain "GROUP BY x LIMIT n" with no ORDER BY at all is just as
        // undefined as the agg_value case: which groups make the cut depends on each engine's
        // internal grouping order, which the SQL standard never promises.
        switch (_rng.Next(3))
        {
            case 0:
                sb.Append(" ORDER BY ").Append(groupCol).Append(" ASC LIMIT ").Append(_rng.Next(1, 50));
                break;
            case 1:
                sb.Append(" ORDER BY agg_value DESC");
                break;
        }

        return sb.ToString();
    }

    private string JoinQuery()
    {
        // Every column is qualified: "category" exists on both sides of this join, and an
        // unqualified reference to it is genuinely ambiguous SQL — SQLite rejects it outright,
        // so the generator never produces it (see DifferentialTests.Join_MatchesSqlite).
        var salesCols = RandomSubset(_sales.Schema.Columns.Select(c => c.Name).ToList(), 1, 3).Select(c => $"s.{c}");
        var brandsCols = RandomSubset(_brands.Schema.Columns.Select(c => c.Name).Where(c => c != "brand").ToList(), 0, 2).Select(c => $"b.{c}");
        var columns = string.Join(", ", salesCols.Concat(brandsCols).DefaultIfEmpty("s.sku"));

        var sb = new System.Text.StringBuilder($"SELECT {columns} FROM fmcg_sales s JOIN brands b ON s.brand = b.brand");

        // WHERE mandatory (bounds comparison cost) and no LIMIT — same reasoning as SimpleSelect:
        // a bare LIMIT on a join result isn't safely comparable across two different engines.
        sb.Append(" WHERE ").Append(RandomPredicate(_sales, _salesSamples, "s"));

        return sb.ToString();
    }

    private string RandomColumnList(Table table)
    {
        if (_rng.Next(4) == 0) return "*";
        var names = RandomSubset(table.Schema.Columns.Select(c => c.Name).ToList(), 1, 5);
        return string.Join(", ", names);
    }

    private string RandomColumnName(Table table) => table.Schema.Columns[_rng.Next(table.Schema.Columns.Count)].Name;

    private string RandomTextColumn(Table table)
    {
        var textCols = table.Schema.Columns.Where(c => c.Type == SqlType.Text).ToList();
        return textCols[_rng.Next(textCols.Count)].Name;
    }

    private string RandomNumericColumn(Table table)
    {
        var numericCols = table.Schema.Columns.Where(c => c.Type is SqlType.Int or SqlType.Decimal).ToList();
        return numericCols[_rng.Next(numericCols.Count)].Name;
    }

    private List<string> RandomSubset(List<string> items, int min, int max)
    {
        var count = Math.Min(items.Count, _rng.Next(min, max + 1));
        return items.OrderBy(_ => _rng.Next()).Take(Math.Max(count, min)).ToList();
    }

    private string RandomPredicate(Table table, Dictionary<string, List<SqlValue>> samples, string? qualifier)
    {
        var first = RandomComparison(table, samples, qualifier);
        if (_rng.Next(3) != 0) return first;

        var op = _rng.Next(2) == 0 ? "AND" : "OR";
        return $"({first} {op} {RandomComparison(table, samples, qualifier)})";
    }

    private string RandomComparison(Table table, Dictionary<string, List<SqlValue>> samples, string? qualifier)
    {
        var column = table.Schema.Columns[_rng.Next(table.Schema.Columns.Count)];
        var qualifiedName = qualifier is null ? column.Name : $"{qualifier}.{column.Name}";
        var value = samples[column.Name][_rng.Next(samples[column.Name].Count)];

        if (column.Type is SqlType.Int or SqlType.Decimal)
        {
            // Every real value in this dataset happens to be non-negative, so without this the
            // negative-literal lexer path (`-5`) would never be exercised by the fuzzer at all —
            // exactly the coverage gap that let a real parser bug ship once already.
            if (_rng.Next(5) == 0) value = Negate(value);

            return _rng.Next(6) switch
            {
                0 => $"{qualifiedName} = {Literal(value)}",
                1 => $"{qualifiedName} != {Literal(value)}",
                2 => $"{qualifiedName} < {Literal(value)}",
                3 => $"{qualifiedName} > {Literal(value)}",
                4 => $"{qualifiedName} >= {Literal(value)}",
                _ => $"{qualifiedName} <= {Literal(value)}",
            };
        }

        return _rng.Next(4) switch
        {
            0 => $"{qualifiedName} = {Literal(value)}",
            1 => $"{qualifiedName} != {Literal(value)}",
            2 => $"{qualifiedName} IS NULL",
            _ => $"{qualifiedName} IS NOT NULL",
        };
    }

    private static SqlValue Negate(SqlValue value) => value.Type switch
    {
        SqlType.Int => SqlValue.Int(-value.AsInt),
        SqlType.Decimal => SqlValue.Decimal(-value.AsDecimal),
        _ => value
    };

    private static string Literal(SqlValue value) => value.Type switch
    {
        SqlType.Text => $"'{value.AsText.Replace("'", "''")}'",
        SqlType.Date => $"'{value.AsDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}'",
        SqlType.Int => value.AsInt.ToString(CultureInfo.InvariantCulture),
        SqlType.Decimal => value.AsDecimal.ToString(CultureInfo.InvariantCulture),
        SqlType.Bool => value.AsBool ? "TRUE" : "FALSE",
        _ => throw new InvalidOperationException($"unreachable: {value.Type}")
    };
}
