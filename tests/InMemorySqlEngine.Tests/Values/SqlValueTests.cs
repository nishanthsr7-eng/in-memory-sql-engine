using InMemorySqlEngine.Core.Values;
using Xunit;

namespace InMemorySqlEngine.Tests.Values;

public class SqlValueTests
{
    [Fact]
    public void NullEqualsNull_IsUnknown_NotTrue()
    {
        var a = SqlValue.Null(SqlType.Int);
        var b = SqlValue.Null(SqlType.Int);
        Assert.Equal(SqlBool.Unknown, a.EqualsSql(b));
    }

    [Fact]
    public void NullNotEqualsValue_IsUnknown()
    {
        var a = SqlValue.Null(SqlType.Int);
        var b = SqlValue.Int(5);
        Assert.Equal(SqlBool.Unknown, a.NotEqualsSql(b));
    }

    [Fact]
    public void IsNull_IsAlwaysDefinite_NeverUnknown()
    {
        var n = SqlValue.Null(SqlType.Text);
        Assert.Equal(SqlBool.True, n.IsNullSql());
        Assert.Equal(SqlBool.False, n.IsNotNullSql());
    }

    [Fact]
    public void IntAndDecimal_CompareNumerically()
    {
        var five = SqlValue.Int(5);
        var fiveDecimal = SqlValue.Decimal(5.0m);
        Assert.Equal(SqlBool.True, five.EqualsSql(fiveDecimal));
    }

    [Fact]
    public void TextComparison_IsOrdinal()
    {
        var a = SqlValue.Text("apple");
        var b = SqlValue.Text("Apple");
        Assert.Equal(SqlBool.False, a.EqualsSql(b));
    }

    /// <summary>
    /// The Equals/GetHashCode contract: since Equals treats Int(5) and Decimal(5.0) as equal
    /// (cross-type numeric equality), GetHashCode must agree for every case Equals does — not
    /// just the ones where the default per-field hash happens to line up. `long.GetHashCode()`
    /// and `decimal.GetHashCode()` disagree for negative values even when numerically equal,
    /// which is exactly the case this regression-tests (found during audit, see git history).
    /// </summary>
    [Theory]
    [InlineData(5, 5.0)]
    [InlineData(0, 0.0)]
    [InlineData(-3, -3.0)]
    [InlineData(-1, -1.0)]
    [InlineData(1000000, 1000000.0)]
    public void IntAndEqualDecimal_HashIdentically(long intValue, double decimalAsDouble)
    {
        var intVal = SqlValue.Int(intValue);
        var decVal = SqlValue.Decimal((decimal)decimalAsDouble);

        Assert.Equal(SqlBool.True, intVal.EqualsSql(decVal));
        Assert.True(intVal.Equals(decVal));
        Assert.Equal(intVal.GetHashCode(), decVal.GetHashCode());
    }

    [Fact]
    public void DictionaryLookup_FindsEqualValueAcrossIntAndDecimal()
    {
        var dict = new Dictionary<SqlValue, string> { [SqlValue.Int(-3)] = "found" };
        Assert.True(dict.TryGetValue(SqlValue.Decimal(-3.0m), out var value));
        Assert.Equal("found", value);
    }

    [Fact]
    public void CompareTo_SortsNullsLast()
    {
        var values = new[] { SqlValue.Int(3), SqlValue.Null(SqlType.Int), SqlValue.Int(1) };
        Array.Sort(values);
        Assert.False(values[0].IsNull);
        Assert.False(values[1].IsNull);
        Assert.True(values[2].IsNull);
    }
}
