using System.Diagnostics;
using Sift.Cli;
using Sift.Core.Catalog;
using Sift.Core.Indexing;
using Sift.Core.Planning;
using Sift.Core.Sql;
using Sift.Core.Sql.Ast;

var repoRoot = FindRepoRoot();
var dataDir = Path.Combine(repoRoot, "data");

Console.WriteLine("Sift — in-memory SQL query engine");

var catalog = new Catalog();
foreach (var csvPath in Directory.EnumerateFiles(dataDir, "*.csv").OrderBy(p => p))
{
    var tableName = Path.GetFileNameWithoutExtension(csvPath);
    var loadStopwatch = Stopwatch.StartNew();
    catalog.AddTable(CsvLoader.Load(csvPath, tableName));
    loadStopwatch.Stop();

    var table = catalog.GetTable(tableName);
    Console.WriteLine($"loaded '{table.Name}': {table.RowCount:N0} rows, {table.Schema.Columns.Count} columns ({loadStopwatch.ElapsedMilliseconds} ms)");
}

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
        foreach (var name in catalog.TableNames) Console.WriteLine(name);
        continue;
    }
    if (input.StartsWith(".schema", StringComparison.OrdinalIgnoreCase))
    {
        var tableName = input[".schema".Length..].Trim();
        if (catalog.TryGetTable(tableName, out var table)) PrintSchema(table.Schema);
        else Console.WriteLine($"unknown table '{tableName}' — try .tables");
        continue;
    }

    var explain = input.StartsWith("EXPLAIN ", StringComparison.OrdinalIgnoreCase);
    if (explain) input = input["EXPLAIN ".Length..].Trim();

    try
    {
        var stopwatch = Stopwatch.StartNew();
        var stmt = Parser.Parse(input);

        switch (stmt)
        {
            case CreateIndexStatement createIndex:
                var kind = createIndex.Kind == IndexTypeHint.Hash ? IndexKind.Hash : IndexKind.BPlusTree;
                catalog.CreateIndex(createIndex.TableName, createIndex.ColumnName, kind);
                stopwatch.Stop();
                Console.WriteLine($"created {kind} index on {createIndex.TableName}({createIndex.ColumnName}) ({stopwatch.ElapsedMilliseconds} ms)");
                break;

            case SelectStatement select:
                var plan = Planner.Plan(select, catalog);
                if (explain)
                {
                    Console.WriteLine(plan.Explain(0));
                    break;
                }
                var rows = plan.Execute().ToList();
                stopwatch.Stop();
                ResultPrinter.Print(plan.OutputSchema, rows);
                Console.WriteLine($"({rows.Count:N0} row{(rows.Count == 1 ? "" : "s")} in {stopwatch.ElapsedMilliseconds} ms)");
                break;
        }
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
