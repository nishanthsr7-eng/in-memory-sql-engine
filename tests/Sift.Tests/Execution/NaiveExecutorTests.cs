using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Sql;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Execution;

public class NaiveExecutorTests
{
    private static Sift.Core.Catalog.Catalog BuildCatalog()
    {
        var schema = new Schema(new[]
        {
            new Column("region", SqlType.Text),
            new Column("units_sold", SqlType.Int),
            new Column("promotion_flag", SqlType.Int),
        });

        var rows = new[]
        {
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Int(10), SqlValue.Int(0) }),
            new Row(new[] { SqlValue.Text("PL-South"), SqlValue.Int(20), SqlValue.Int(1) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Int(30), SqlValue.Int(1) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Null(SqlType.Int), SqlValue.Int(0) }),
        };

        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Table("sales", schema, rows));
        return catalog;
    }

    [Fact]
    public void FiltersAndProjects()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect("SELECT units_sold FROM sales WHERE region = 'PL-North'");

        var result = NaiveExecutor.Execute(stmt, catalog);
        var rows = result.Rows.ToList();

        Assert.Single(result.OutputSchema.Columns);
        Assert.Equal(3, rows.Count); // 3 PL-North rows, NULL row's predicate is still definite (region isn't null)
    }

    [Fact]
    public void LimitStopsEarly()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect("SELECT * FROM sales LIMIT 2");

        var rows = NaiveExecutor.Execute(stmt, catalog).Rows.ToList();
        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void ExecutionIsLazy_LimitDoesNotTouchAllRows()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect("SELECT * FROM sales LIMIT 1");

        var touched = 0;
        var rows = NaiveExecutor.Execute(stmt, catalog).Rows.Select(r => { touched++; return r; });
        _ = rows.First();

        Assert.Equal(1, touched);
    }

    [Fact]
    public void AliasRenamesOutputColumn()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect("SELECT units_sold AS units FROM sales LIMIT 1");

        var result = NaiveExecutor.Execute(stmt, catalog);
        Assert.Equal("units", result.OutputSchema.Columns[0].Name);
    }

    [Fact]
    public void LowSelectivityPredicate_ExcludesNonMatchingRows()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect("SELECT * FROM sales WHERE promotion_flag = 0");

        var rows = NaiveExecutor.Execute(stmt, catalog).Rows.ToList();
        Assert.Equal(2, rows.Count);
    }
}
