using Sift.Core.Values;

namespace Sift.Core.Catalog;

/// <summary>
/// Per-table stats collected once at load time — row count and a per-column distinct-value
/// count. That's all the cost model needs: selectivity for `=` is `1 / distinctValues`
/// (PLAN.md §6). Real systems keep histograms for skewed data; this is the deliberately
/// simple next step up from "assume everything is uniform."
/// </summary>
public sealed class Statistics
{
    public int RowCount { get; }
    private readonly Dictionary<string, int> _distinctCounts;

    private Statistics(int rowCount, Dictionary<string, int> distinctCounts)
    {
        RowCount = rowCount;
        _distinctCounts = distinctCounts;
    }

    /// <summary>Distinct non-null values in the column — never less than 1, so selectivity math never divides by zero.</summary>
    public int DistinctCount(string columnName) =>
        _distinctCounts.TryGetValue(columnName, out var count) ? count : Math.Max(RowCount, 1);

    public static Statistics Collect(Schema schema, IReadOnlyList<Row> rows)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var c = 0; c < schema.Columns.Count; c++)
        {
            var distinct = new HashSet<SqlValue>();
            foreach (var row in rows)
            {
                var value = row[c];
                if (!value.IsNull) distinct.Add(value);
            }
            counts[schema.Columns[c].Name] = Math.Max(distinct.Count, 1);
        }
        return new Statistics(rows.Count, counts);
    }
}
