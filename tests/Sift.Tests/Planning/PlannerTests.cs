using Sift.Core.Catalog;
using Sift.Core.Planning;
using Sift.Core.Sql;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Planning;

public class PlannerTests
{
    private static Sift.Core.Catalog.Catalog BuildCatalog()
    {
        var salesSchema = new Schema(new[]
        {
            new Column("region", SqlType.Text),
            new Column("brand", SqlType.Text),
            new Column("units_sold", SqlType.Int),
            new Column("promotion_flag", SqlType.Int),
        });
        var salesRows = new[]
        {
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("MiBrand1"), SqlValue.Int(10), SqlValue.Int(0) }),
            new Row(new[] { SqlValue.Text("PL-South"), SqlValue.Text("MiBrand1"), SqlValue.Int(20), SqlValue.Int(1) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("YoBrand1"), SqlValue.Int(30), SqlValue.Int(1) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("MiBrand1"), SqlValue.Int(40), SqlValue.Int(0) }),
        };

        var brandsSchema = new Schema(new[]
        {
            new Column("brand", SqlType.Text),
            new Column("category", SqlType.Text),
        });
        var brandsRows = new[]
        {
            new Row(new[] { SqlValue.Text("MiBrand1"), SqlValue.Text("Milk") }),
            new Row(new[] { SqlValue.Text("YoBrand1"), SqlValue.Text("Yogurt") }),
        };

        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Table("sales", salesSchema, salesRows));
        catalog.AddTable(new Table("brands", brandsSchema, brandsRows));
        return catalog;
    }

    private static List<Row> Run(string sql, Sift.Core.Catalog.Catalog catalog, out Schema schema)
    {
        var stmt = Parser.ParseSelect(sql);
        var plan = Planner.Plan(stmt, catalog);
        schema = plan.OutputSchema;
        return plan.Execute().ToList();
    }

    [Fact]
    public void FiltersProjectsAndLimits()
    {
        var rows = Run("SELECT region, units_sold FROM sales WHERE promotion_flag = 1 LIMIT 1", BuildCatalog(), out var schema);

        Assert.Equal(2, schema.Columns.Count);
        Assert.Single(rows);
    }

    [Fact]
    public void GroupByWithAggregateAndAlias()
    {
        var rows = Run("SELECT brand, SUM(units_sold) AS total FROM sales GROUP BY brand", BuildCatalog(), out var schema);

        Assert.Equal("total", schema.Columns[1].Name);
        var byBrand = rows.ToDictionary(r => r[0].AsText, r => r[1].AsDecimal);
        Assert.Equal(70m, byBrand["MiBrand1"]); // 10 + 20 + 40
        Assert.Equal(30m, byBrand["YoBrand1"]);
    }

    [Fact]
    public void HavingFiltersGroups()
    {
        var rows = Run(
            "SELECT brand, SUM(units_sold) AS total FROM sales GROUP BY brand HAVING SUM(units_sold) > 50",
            BuildCatalog(), out _);

        var brand = Assert.Single(rows);
        Assert.Equal("MiBrand1", brand[0].AsText);
    }

    [Fact]
    public void OrderByDescending()
    {
        var rows = Run("SELECT brand, units_sold FROM sales ORDER BY units_sold DESC", BuildCatalog(), out _);

        Assert.Equal(new[] { 40L, 30L, 20L, 10L }, rows.Select(r => r[1].AsInt));
    }

    [Fact]
    public void JoinCombinesMatchingRowsOnly()
    {
        var rows = Run(
            "SELECT s.brand, category FROM sales s JOIN brands b ON s.brand = b.brand",
            BuildCatalog(), out var schema);

        Assert.Equal(4, rows.Count); // every sales row has a matching brand in this fixture
        Assert.Contains(schema.Columns, c => c.Name == "category");
    }

    [Fact]
    public void CountStarWithNoGroupByOnEmptyResult_ReturnsOneZeroRow()
    {
        var rows = Run("SELECT COUNT(*) AS n FROM sales WHERE region = 'nowhere'", BuildCatalog(), out _);

        var row = Assert.Single(rows);
        Assert.Equal(0L, row[0].AsInt);
    }

    [Fact]
    public void FullPipeline_FilterGroupHavingOrderLimit()
    {
        var rows = Run(
            "SELECT brand, COUNT(*) AS n, SUM(units_sold) AS total FROM sales " +
            "WHERE units_sold > 5 " +
            "GROUP BY brand " +
            "HAVING COUNT(*) >= 1 " +
            "ORDER BY total DESC " +
            "LIMIT 1",
            BuildCatalog(), out var schema);

        Assert.Equal(new[] { "brand", "n", "total" }, schema.Columns.Select(c => c.Name));
        var row = Assert.Single(rows);
        Assert.Equal("MiBrand1", row[0].AsText);
        Assert.Equal(70m, row[2].AsDecimal);
    }
}
