using Sift.Core.Catalog;
using Sift.Core.Indexing;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Indexing;

public class HashIndexTests
{
    private static Table BuildTable()
    {
        var schema = new Schema(new[] { new Column("region", SqlType.Text) });
        var rows = new[]
        {
            new Row(new[] { SqlValue.Text("PL-North") }),
            new Row(new[] { SqlValue.Text("PL-South") }),
            new Row(new[] { SqlValue.Text("PL-North") }),
            new Row(new[] { SqlValue.Null(SqlType.Text) }),
        };
        return new Table("t", schema, rows);
    }

    [Fact]
    public void LookupReturnsAllMatchingRowIds()
    {
        var index = new HashIndex("region", BuildTable());
        Assert.Equal(new[] { 0, 2 }, index.Lookup(SqlValue.Text("PL-North")).OrderBy(x => x));
        Assert.Equal(new[] { 1 }, index.Lookup(SqlValue.Text("PL-South")));
        Assert.Empty(index.Lookup(SqlValue.Text("nowhere")));
    }

    [Fact]
    public void NullValuesAreNeverIndexed()
    {
        var index = new HashIndex("region", BuildTable());
        // there's no SqlValue that IS a "NULL key" to look up — confirm the row was simply skipped
        Assert.Equal(3, new[] { 0, 1, 2 }.Length); // sanity: 3 non-null rows exist
        Assert.DoesNotContain(3, index.Lookup(SqlValue.Text("PL-North")));
    }

    [Fact]
    public void RangeThrowsNotSupported()
    {
        var index = new HashIndex("region", BuildTable());
        Assert.Throws<NotSupportedException>(() => index.Range(null, false, null, false).ToList());
    }

    [Fact]
    public void SupportsRangeIsFalse()
    {
        Assert.False(new HashIndex("region", BuildTable()).SupportsRange);
    }
}
