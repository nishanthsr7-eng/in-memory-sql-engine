using Sift.Core.Indexing;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Catalog;

public class CatalogIndexTests
{
    private static Sift.Core.Catalog.Catalog BuildCatalog()
    {
        var schema = new Sift.Core.Catalog.Schema(new[] { new Sift.Core.Catalog.Column("Region", SqlType.Text) });
        var rows = new[] { new Sift.Core.Catalog.Row(new[] { SqlValue.Text("PL-North") }) };
        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Sift.Core.Catalog.Table("t", schema, rows));
        return catalog;
    }

    [Fact]
    public void CreateIndex_ThenTryGetIndex_RoundTrips()
    {
        var catalog = BuildCatalog();
        catalog.CreateIndex("t", "Region", IndexKind.BPlusTree);

        Assert.True(catalog.TryGetIndex("t", "Region", out var index));
        Assert.Equal(IndexKind.BPlusTree, index.Kind);
    }

    [Fact]
    public void IndexLookup_IsCaseInsensitiveOnTableAndColumnName()
    {
        var catalog = BuildCatalog();
        catalog.CreateIndex("t", "region", IndexKind.Hash); // created using lowercase column name

        Assert.True(catalog.TryGetIndex("T", "REGION", out var index)); // looked up with different casing
        Assert.Equal(IndexKind.Hash, index.Kind);
    }

    [Fact]
    public void TryGetIndex_ReturnsFalseWhenNoneCreated()
    {
        var catalog = BuildCatalog();
        Assert.False(catalog.TryGetIndex("t", "Region", out _));
    }
}
