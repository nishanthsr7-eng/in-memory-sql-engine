using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Values;

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
