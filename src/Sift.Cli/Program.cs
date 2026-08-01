using System.Diagnostics;
using Sift.Cli;
using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Sql;

var repoRoot = FindRepoRoot();
var dataPath = Path.Combine(repoRoot, "data", "fmcg_sales.csv");

Console.WriteLine("Sift — in-memory SQL query engine");
Console.WriteLine($"loading {dataPath} ...");

var loadStopwatch = Stopwatch.StartNew();
var catalog = new Catalog();
catalog.AddTable(CsvLoader.Load(dataPath, "fmcg_sales"));
loadStopwatch.Stop();

var loaded = catalog.GetTable("fmcg_sales");
Console.WriteLine($"loaded '{loaded.Name}': {loaded.RowCount:N0} rows, {loaded.Schema.Columns.Count} columns ({loadStopwatch.ElapsedMilliseconds} ms)");
Console.WriteLine("enter a SQL query, or .exit to quit");
Console.WriteLine();

while (true)
{
    Console.Write("sift> ");
    var input = Console.ReadLine();
    if (input is null) break;

    input = input.Trim();
    if (input.Length == 0) continue;

    if (input is ".exit" or ".quit") break;
    if (input == ".tables")
    {
        Console.WriteLine(loaded.Name);
        continue;
    }
    if (input.StartsWith(".schema", StringComparison.OrdinalIgnoreCase))
    {
        PrintSchema(loaded.Schema);
        continue;
    }

    try
    {
        var stopwatch = Stopwatch.StartNew();
        var stmt = Parser.ParseSelect(input);
        var result = NaiveExecutor.Execute(stmt, catalog);
        var rows = result.Rows.ToList();
        stopwatch.Stop();

        ResultPrinter.Print(result.OutputSchema, rows);
        Console.WriteLine($"({rows.Count:N0} row{(rows.Count == 1 ? "" : "s")} in {stopwatch.ElapsedMilliseconds} ms)");
    }
    catch (SqlParseException ex)
    {
        Console.WriteLine($"syntax error: {ex.Message}");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"error: {ex.Message}");
    }

    Console.WriteLine();
}

static string FindRepoRoot()
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

static void PrintSchema(Schema schema)
{
    foreach (var column in schema.Columns)
        Console.WriteLine($"  {column.Name} {column.Type}");
}
