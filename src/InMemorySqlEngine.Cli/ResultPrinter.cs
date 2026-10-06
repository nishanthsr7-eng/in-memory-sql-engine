using InMemorySqlEngine.Core.Catalog;

namespace InMemorySqlEngine.Cli;

internal static class ResultPrinter
{
    public static void Print(Schema schema, IReadOnlyList<Row> rows)
    {
        var columnCount = schema.Columns.Count;
        var widths = new int[columnCount];
        for (var c = 0; c < columnCount; c++)
            widths[c] = schema.Columns[c].Name.Length;

        var cells = new string[rows.Count][];
        for (var r = 0; r < rows.Count; r++)
        {
            cells[r] = new string[columnCount];
            for (var c = 0; c < columnCount; c++)
            {
                var text = rows[r][c].ToString();
                cells[r][c] = text;
                if (text.Length > widths[c]) widths[c] = text.Length;
            }
        }

        PrintRow(schema.Columns.Select(col => col.Name).ToArray(), widths);
        Console.WriteLine(string.Join("-+-", widths.Select(w => new string('-', w))));
        foreach (var row in cells)
            PrintRow(row, widths);
    }

    private static void PrintRow(IReadOnlyList<string> cells, int[] widths)
    {
        Console.WriteLine(string.Join(" | ", cells.Select((cell, i) => cell.PadRight(widths[i]))));
    }
}
