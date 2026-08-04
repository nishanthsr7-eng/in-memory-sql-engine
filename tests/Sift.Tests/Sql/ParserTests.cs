using Sift.Core.Sql;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Sql;

public class ParserTests
{
    [Fact]
    public void ParsesStar_FromAndLimit()
    {
        var stmt = Parser.ParseSelect("SELECT * FROM t LIMIT 5");

        Assert.True(stmt.Columns is [{ IsStar: true }]);
        Assert.Equal("t", stmt.From.Name);
        Assert.Null(stmt.From.Alias);
        Assert.Equal(5, stmt.Limit);
        Assert.Null(stmt.Where);
    }

    [Fact]
    public void ParsesColumnListWithAliasAndTableAlias()
    {
        var stmt = Parser.ParseSelect("SELECT brand AS b, units_sold FROM fmcg_sales f");

        Assert.Equal(2, stmt.Columns.Count);
        Assert.Equal("brand", Assert.IsType<ColumnRefExpr>(stmt.Columns[0].Expression).ColumnName);
        Assert.Equal("b", stmt.Columns[0].Alias);
        Assert.Equal("units_sold", Assert.IsType<ColumnRefExpr>(stmt.Columns[1].Expression).ColumnName);
        Assert.Null(stmt.Columns[1].Alias);
        Assert.Equal("f", stmt.From.Alias);
    }

    [Fact]
    public void ParsesEqualityPredicate()
    {
        var stmt = Parser.ParseSelect("SELECT * FROM t WHERE region = 'PL-North'");

        var cmp = Assert.IsType<ComparisonExpr>(stmt.Where);
        Assert.Equal(ComparisonOp.Eq, cmp.Op);
        var col = Assert.IsType<ColumnRefExpr>(cmp.Left);
        Assert.Equal("region", col.ColumnName);
        var lit = Assert.IsType<LiteralExpr>(cmp.Right);
        Assert.Equal(SqlValue.Text("PL-North").ToString(), lit.Value.ToString());
    }

    [Fact]
    public void ParsesAndOrWithCorrectPrecedence()
    {
        // AND binds tighter than OR: a OR (b AND c)
        var stmt = Parser.ParseSelect("SELECT * FROM t WHERE a = 1 OR b = 2 AND c = 3");

        var top = Assert.IsType<LogicalExpr>(stmt.Where);
        Assert.Equal(LogicalOp.Or, top.Op);
        Assert.IsType<ComparisonExpr>(top.Left);
        var right = Assert.IsType<LogicalExpr>(top.Right);
        Assert.Equal(LogicalOp.And, right.Op);
    }

    [Fact]
    public void ParsesBetween()
    {
        var stmt = Parser.ParseSelect("SELECT * FROM t WHERE price BETWEEN 1 AND 10");
        var between = Assert.IsType<BetweenExpr>(stmt.Where);
        Assert.Equal("price", Assert.IsType<ColumnRefExpr>(between.Value).ColumnName);
    }

    [Fact]
    public void ParsesIn()
    {
        var stmt = Parser.ParseSelect("SELECT * FROM t WHERE region IN ('A', 'B', 'C')");
        var inExpr = Assert.IsType<InExpr>(stmt.Where);
        Assert.Equal(3, inExpr.Items.Count);
    }

    [Fact]
    public void ParsesIsNullAndIsNotNull()
    {
        var isNull = Parser.ParseSelect("SELECT * FROM t WHERE x IS NULL").Where;
        Assert.False(Assert.IsType<IsNullExpr>(isNull).Negated);

        var isNotNull = Parser.ParseSelect("SELECT * FROM t WHERE x IS NOT NULL").Where;
        Assert.True(Assert.IsType<IsNullExpr>(isNotNull).Negated);
    }

    [Fact]
    public void ParsesParenthesizedGrouping()
    {
        var stmt = Parser.ParseSelect("SELECT * FROM t WHERE (a = 1 OR b = 2) AND c = 3");
        var top = Assert.IsType<LogicalExpr>(stmt.Where);
        Assert.Equal(LogicalOp.And, top.Op);
        Assert.IsType<LogicalExpr>(top.Left);
    }

    [Fact]
    public void ThrowsOnMalformedInput()
    {
        Assert.Throws<SqlParseException>(() => Parser.ParseSelect("SELECT FROM t"));
        Assert.Throws<SqlParseException>(() => Parser.ParseSelect("SELECT * t"));
        Assert.Throws<SqlParseException>(() => Parser.ParseSelect("SELECT * FROM"));
    }

    [Fact]
    public void ParsesJoinWithOnClause()
    {
        var stmt = Parser.ParseSelect(
            "SELECT * FROM fmcg_sales s JOIN brands b ON s.brand = b.brand");

        Assert.Single(stmt.Joins);
        var join = stmt.Joins[0];
        Assert.Equal("brands", join.Table.Name);
        Assert.Equal("b", join.Table.Alias);
        Assert.Equal(("s", "brand"), (join.LeftColumn.Qualifier, join.LeftColumn.ColumnName));
        Assert.Equal(("b", "brand"), (join.RightColumn.Qualifier, join.RightColumn.ColumnName));
    }

    [Fact]
    public void ParsesAggregateWithAlias()
    {
        var stmt = Parser.ParseSelect("SELECT brand, SUM(units_sold) AS total FROM t GROUP BY brand");

        Assert.Equal("brand", Assert.IsType<ColumnRefExpr>(stmt.Columns[0].Expression).ColumnName);
        var agg = Assert.IsType<AggregateExpr>(stmt.Columns[1].Expression);
        Assert.Equal(AggregateFunc.Sum, agg.Func);
        Assert.Equal("units_sold", agg.Argument!.ColumnName);
        Assert.Equal("total", stmt.Columns[1].Alias);

        Assert.Single(stmt.GroupBy);
        Assert.Equal("brand", stmt.GroupBy[0].ColumnName);
    }

    [Fact]
    public void ParsesCountStar()
    {
        var stmt = Parser.ParseSelect("SELECT COUNT(*) FROM t");
        var agg = Assert.IsType<AggregateExpr>(stmt.Columns[0].Expression);
        Assert.True(agg.IsCountStar);
        Assert.Equal(AggregateFunc.Count, agg.Func);
        Assert.Equal("COUNT(*)", agg.CanonicalName);
    }

    [Fact]
    public void RejectsStarForNonCountAggregate()
    {
        Assert.Throws<SqlParseException>(() => Parser.ParseSelect("SELECT SUM(*) FROM t"));
    }

    [Fact]
    public void ParsesHavingWithAggregate()
    {
        var stmt = Parser.ParseSelect(
            "SELECT brand, SUM(units_sold) AS total FROM t GROUP BY brand HAVING SUM(units_sold) > 100");

        var cmp = Assert.IsType<ComparisonExpr>(stmt.Having);
        var agg = Assert.IsType<AggregateExpr>(cmp.Left);
        Assert.Equal("SUM(units_sold)", agg.CanonicalName);
    }

    [Fact]
    public void ParsesOrderByWithDirection()
    {
        var stmt = Parser.ParseSelect("SELECT * FROM t ORDER BY units_sold DESC, brand ASC");

        Assert.Equal(2, stmt.OrderBy.Count);
        Assert.Equal("units_sold", stmt.OrderBy[0].Column.ColumnName);
        Assert.True(stmt.OrderBy[0].Descending);
        Assert.Equal("brand", stmt.OrderBy[1].Column.ColumnName);
        Assert.False(stmt.OrderBy[1].Descending);
    }

    [Fact]
    public void ParsesFullPipeline()
    {
        var stmt = Parser.ParseSelect(
            "SELECT region, COUNT(*) AS n FROM fmcg_sales " +
            "WHERE promotion_flag = 1 " +
            "GROUP BY region " +
            "HAVING COUNT(*) > 10 " +
            "ORDER BY n DESC " +
            "LIMIT 5");

        Assert.NotNull(stmt.Where);
        Assert.Single(stmt.GroupBy);
        Assert.NotNull(stmt.Having);
        Assert.Single(stmt.OrderBy);
        Assert.Equal(5, stmt.Limit);
    }
}
