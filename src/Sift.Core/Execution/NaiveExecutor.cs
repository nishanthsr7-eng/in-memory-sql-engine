using Sift.Core.Catalog;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Execution;

/// <summary>
/// Phase 1 executor: scan every row, evaluate the predicate, project columns. Deliberately
/// unoptimized — no operator tree, no indexes, no plan. Replaced in Phase 2 by the lazy
/// Volcano-style operator model; this stays only as the "does the whole pipeline work" baseline.
/// </summary>
public static class NaiveExecutor
{
    public static QueryResult Execute(SelectStatement stmt, Catalog.Catalog catalog)
    {
        var table = catalog.GetTable(stmt.From.Name);
        var (outputSchema, sourceIndices) = BuildProjection(stmt.Columns, table.Schema);
        return new QueryResult(outputSchema, Run(stmt, table, outputSchema, sourceIndices));
    }

    private static IEnumerable<Row> Run(SelectStatement stmt, Table table, Schema outputSchema, int[] sourceIndices)
    {
        var returned = 0;
        foreach (var row in table.Rows)
        {
            if (stmt.Limit is { } limit && returned >= limit) yield break;

            if (stmt.Where is not null && !Evaluator.EvalPredicate(stmt.Where, row, table.Schema).IsTrue())
                continue;

            yield return Project(row, sourceIndices);
            returned++;
        }
    }

    private static Row Project(Row row, int[] sourceIndices)
    {
        var values = new SqlValue[sourceIndices.Length];
        for (var i = 0; i < sourceIndices.Length; i++)
            values[i] = row[sourceIndices[i]];
        return new Row(values);
    }

    private static (Schema OutputSchema, int[] SourceIndices) BuildProjection(
        IReadOnlyList<SelectItem> items, Schema tableSchema)
    {
        if (items.Count == 1 && items[0].IsStar)
        {
            var sourceIndices = Enumerable.Range(0, tableSchema.Columns.Count).ToArray();
            return (tableSchema, sourceIndices);
        }

        var columns = new Column[items.Count];
        var indices = new int[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var item = items[i];
            indices[i] = tableSchema.IndexOf(item.ColumnName!);
            var sourceColumn = tableSchema.Columns[indices[i]];
            columns[i] = new Column(item.Alias ?? item.ColumnName!, sourceColumn.Type);
        }
        return (new Schema(columns), indices);
    }
}

public sealed record QueryResult(Schema OutputSchema, IEnumerable<Row> Rows);
