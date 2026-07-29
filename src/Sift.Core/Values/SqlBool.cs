namespace Sift.Core.Values;

/// <summary>SQL three-valued logic: NULL comparisons yield Unknown, not False.</summary>
public enum SqlBool
{
    False = 0,
    True = 1,
    Unknown = 2
}

public static class SqlBoolExtensions
{
    public static SqlBool FromBool(bool value) => value ? SqlBool.True : SqlBool.False;

    /// <summary>Only Unknown escapes: a WHERE clause keeps a row only when this is True.</summary>
    public static bool IsTrue(this SqlBool value) => value == SqlBool.True;

    public static SqlBool Not(this SqlBool value) => value switch
    {
        SqlBool.True => SqlBool.False,
        SqlBool.False => SqlBool.True,
        _ => SqlBool.Unknown
    };

    public static SqlBool And(this SqlBool left, SqlBool right)
    {
        if (left == SqlBool.False || right == SqlBool.False) return SqlBool.False;
        if (left == SqlBool.Unknown || right == SqlBool.Unknown) return SqlBool.Unknown;
        return SqlBool.True;
    }

    public static SqlBool Or(this SqlBool left, SqlBool right)
    {
        if (left == SqlBool.True || right == SqlBool.True) return SqlBool.True;
        if (left == SqlBool.Unknown || right == SqlBool.Unknown) return SqlBool.Unknown;
        return SqlBool.False;
    }
}
