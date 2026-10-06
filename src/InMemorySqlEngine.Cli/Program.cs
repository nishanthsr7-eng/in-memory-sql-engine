using System.Diagnostics;
using InMemorySqlEngine.Cli;
using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Indexing;
using InMemorySqlEngine.Core.Planning;
using InMemorySqlEngine.Core.Sql;
using InMemorySqlEngine.Core.Sql.Ast;

// Usage: InMemorySqlEngine.Cli [--data <dir>] [file.csv ...]
// With no arguments, loads every CSV in the repo's data/ directory.
var csvPaths = ResolveCsvPaths(args);

Console.WriteLine("In-Memory SQL Engine");

var catalog = new Catalog();
foreach (var csvPath in csvPaths)
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
    Console.Write("sql> ");
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

static List<string> ResolveCsvPaths(string[] args)
{
    var paths = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] == "--data")
        {
            if (i + 1 >= args.Length) throw new ArgumentException("--data needs a directory");
            var dir = args[++i];
            if (!Directory.Exists(dir)) throw new DirectoryNotFoundException($"data directory not found: {dir}");
            paths.AddRange(Directory.EnumerateFiles(dir, "*.csv").OrderBy(p => p));
        }
        else if (File.Exists(args[i]))
        {
            paths.Add(args[i]);
        }
        else
        {
            throw new FileNotFoundException($"not a CSV file or option: {args[i]}");
        }
    }

    if (paths.Count == 0)
        paths.AddRange(Directory.EnumerateFiles(Path.Combine(FindRepoRoot(), "data"), "*.csv").OrderBy(p => p));
    return paths;
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
    throw new DirectoryNotFoundException("could not locate repo root (looked for sibling 'data' and 'src' directories) — pass --data <dir> or CSV paths");
}

static void PrintSchema(Schema schema)
{
    foreach (var column in schema.Columns)
        Console.WriteLine($"  {column.Name} {column.Type}");
}
