using InMemorySqlEngine.Core.Sql;
using InMemorySqlEngine.Core.Sql.Ast;
using Xunit;

namespace InMemorySqlEngine.Tests.Sql;

public class CreateIndexParserTests
{
    [Fact]
    public void ParsesDefaultingToBTree()
    {
        var stmt = Assert.IsType<CreateIndexStatement>(Parser.Parse("CREATE INDEX ON fmcg_sales(region)"));
        Assert.Equal("fmcg_sales", stmt.TableName);
        Assert.Equal("region", stmt.ColumnName);
        Assert.Equal(IndexTypeHint.BTree, stmt.Kind);
    }

    [Fact]
    public void ParsesExplicitUsingHash()
    {
        var stmt = Assert.IsType<CreateIndexStatement>(Parser.Parse("CREATE INDEX ON t(col) USING HASH"));
        Assert.Equal(IndexTypeHint.Hash, stmt.Kind);
    }

    [Fact]
    public void ParsesExplicitUsingBTree()
    {
        var stmt = Assert.IsType<CreateIndexStatement>(Parser.Parse("CREATE INDEX ON t(col) USING BTREE"));
        Assert.Equal(IndexTypeHint.BTree, stmt.Kind);
    }

    [Fact]
    public void DispatchAlsoParsesSelect()
    {
        Assert.IsType<SelectStatement>(Parser.Parse("SELECT * FROM t"));
    }

    [Fact]
    public void RejectsUnknownIndexType()
    {
        Assert.Throws<SqlParseException>(() => Parser.Parse("CREATE INDEX ON t(col) USING BOGUS"));
    }
}
