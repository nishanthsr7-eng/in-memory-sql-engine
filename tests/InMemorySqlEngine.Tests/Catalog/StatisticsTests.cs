using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Values;
using Xunit;

namespace InMemorySqlEngine.Tests.Catalog;

public class StatisticsTests
{
    [Fact]
    public void DistinctCount_ExcludesNulls_AndIsAtLeastOne()
    {
        var schema = new Schema(new[] { new Column("flag", SqlType.Int), new Column("always_null", SqlType.Int) });
        var rows = new[]
        {
            new Row(new[] { SqlValue.Int(0), SqlValue.Null(SqlType.Int) }),
            new Row(new[] { SqlValue.Int(1), SqlValue.Null(SqlType.Int) }),
            new Row(new[] { SqlValue.Int(0), SqlValue.Null(SqlType.Int) }),
        };
        var stats = Statistics.Collect(schema, rows);

        Assert.Equal(3, stats.RowCount);
        Assert.Equal(2, stats.DistinctCount("flag"));
        Assert.Equal(1, stats.DistinctCount("always_null")); // all-NULL column still reports >= 1
    }

    [Fact]
    public void UnknownColumn_FallsBackToRowCount()
    {
        var schema = new Schema(new[] { new Column("x", SqlType.Int) });
        var rows = new[] { new Row(new[] { SqlValue.Int(1) }) };
        var stats = Statistics.Collect(schema, rows);

        Assert.Equal(1, stats.DistinctCount("nonexistent"));
    }
}
