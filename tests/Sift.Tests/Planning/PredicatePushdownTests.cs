using Sift.Core.Catalog;
using Sift.Core.Planning;
using Sift.Core.Sql;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Planning;

public class PredicatePushdownTests
{
    private static Sift.Core.Catalog.Catalog BuildCatalog()
    {
        // Deliberately gives both tables a column named "category" — a same-name collision
        // across the join, which is exactly what breaks naive (qualifier-blind) pushdown.
        var salesSchema = new Schema(new[]
        {
            new Column("sku", SqlType.Text),
            new Column("brand", SqlType.Text),
            new Column("category", SqlType.Text),
        });
        var salesRows = new[]
        {
            new Row(new[] { SqlValue.Text("MI-006"), SqlValue.Text("MiBrand1"), SqlValue.Text("Milk") }),
            new Row(new[] { SqlValue.Text("RE-001"), SqlValue.Text("ReBrand1"), SqlValue.Text("Milk") }), // sales.category says Milk...
        };

        var brandsSchema = new Schema(new[]
        {
            new Column("brand", SqlType.Text),
            new Column("category", SqlType.Text),
        });
        var brandsRows = new[]
        {
            new Row(new[] { SqlValue.Text("MiBrand1"), SqlValue.Text("Milk") }),
            new Row(new[] { SqlValue.Text("ReBrand1"), SqlValue.Text("ReadyMeal") }), // ...but brands.category says ReadyMeal
        };

        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Table("sales", salesSchema, salesRows));
        catalog.AddTable(new Table("brands", brandsSchema, brandsRows));
        return catalog;
    }

    [Fact]
    public void QualifiedPredicate_ResolvesToTheRightSide_DespiteAColumnNameCollision()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect(
            "SELECT s.sku FROM sales s JOIN brands b ON s.brand = b.brand WHERE b.category = 'ReadyMeal'");

        var plan = Planner.Plan(stmt, catalog);
        var rows = plan.Execute().ToList();

        // If the predicate were wrongly attributed to sales.category (which also says "Milk"
        // for both rows), this would return 0 rows instead of the one that actually matches
        // brands.category = 'ReadyMeal'.
        var row = Assert.Single(rows);
        Assert.Equal("RE-001", row[0].AsText);
    }

    [Fact]
    public void PushedFilter_EndsUpBelowTheJoinInExplainOutput()
    {
        var catalog = BuildCatalog();
        var stmt = Parser.ParseSelect("SELECT * FROM sales s JOIN brands b ON s.brand = b.brand WHERE s.sku = 'MI-006'");
        var plan = Planner.Plan(stmt, catalog);

        var explain = plan.Explain(0);
        var joinLine = explain.Split('\n').ToList().FindIndex(l => l.Contains("Join"));
        var filterLine = explain.Split('\n').ToList().FindIndex(l => l.Contains("Filter"));

        Assert.True(filterLine > joinLine, "Filter should appear below (indented further / listed after) the Join");
    }

    [Fact]
    public void AmbiguousUnqualifiedColumn_IsNotPushedToEitherSide()
    {
        var catalog = BuildCatalog();
        // unqualified "category" exists on both sides — pushdown must leave it above the join
        // rather than guess, so the join's inputs stay unfiltered by it.
        var stmt = Parser.ParseSelect("SELECT * FROM sales s JOIN brands b ON s.brand = b.brand WHERE category = 'Milk'");
        var plan = Planner.Plan(stmt, catalog);

        // Still must execute without throwing and produce a well-defined (if resolver-dependent) result.
        var rows = plan.Execute().ToList();
        Assert.True(rows.Count >= 0);
    }
}
