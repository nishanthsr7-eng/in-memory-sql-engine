using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Values;
using Xunit;

namespace InMemorySqlEngine.Tests.Catalog;

public class CsvLoaderTests
{
    [Fact]
    public void InfersTypesAndParsesNulls()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path,
                "id,price,name,active,signup_date\n" +
                "1,2.50,Alice,true,2022-01-21\n" +
                "2,,Bob,false,2022-02-01\n" + // empty price -> NULL
                "3,10,Carol,true,2022-03-15\n");

            var table = CsvLoader.Load(path, "t");

            Assert.Equal(SqlType.Int, table.Schema.Get("id").Type);
            Assert.Equal(SqlType.Decimal, table.Schema.Get("price").Type); // widened by "2.50"
            Assert.Equal(SqlType.Text, table.Schema.Get("name").Type);
            Assert.Equal(SqlType.Bool, table.Schema.Get("active").Type);
            Assert.Equal(SqlType.Date, table.Schema.Get("signup_date").Type);

            Assert.Equal(3, table.RowCount);
            var priceIdx = table.Schema.IndexOf("price");
            Assert.True(table.GetRow(1)[priceIdx].IsNull);
            Assert.Equal(2.50m, table.GetRow(0)[priceIdx].AsDecimal);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
