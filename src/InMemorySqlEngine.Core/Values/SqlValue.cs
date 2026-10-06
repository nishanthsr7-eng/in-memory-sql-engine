using System.Globalization;

namespace InMemorySqlEngine.Core.Values;

/// <summary>
/// A typed SQL scalar with an explicit null flag. Deliberately not C# `object`/`bool?` —
/// keeps comparisons free of boxing and makes NULL-ness a first-class, checked property
/// instead of something callers can silently forget to test for.
/// </summary>
public readonly struct SqlValue : IEquatable<SqlValue>, IComparable<SqlValue>
{
    public SqlType Type { get; }
    public bool IsNull { get; }

    private readonly long _intValue;
    private readonly decimal _decimalValue;
    private readonly string? _textValue;
    private readonly DateTime _dateValue;
    private readonly bool _boolValue;

    private SqlValue(SqlType type, bool isNull, long intValue = 0, decimal decimalValue = 0,
        string? textValue = null, DateTime dateValue = default, bool boolValue = false)
    {
        Type = type;
        IsNull = isNull;
        _intValue = intValue;
        _decimalValue = decimalValue;
        _textValue = textValue;
        _dateValue = dateValue;
        _boolValue = boolValue;
    }

    public static SqlValue Int(long value) => new(SqlType.Int, isNull: false, intValue: value);
    public static SqlValue Decimal(decimal value) => new(SqlType.Decimal, isNull: false, decimalValue: value);
    public static SqlValue Text(string value) => new(SqlType.Text, isNull: false, textValue: value);
    public static SqlValue Date(DateTime value) => new(SqlType.Date, isNull: false, dateValue: value.Date);
    public static SqlValue Bool(bool value) => new(SqlType.Bool, isNull: false, boolValue: value);
    public static SqlValue Null(SqlType type) => new(type, isNull: true);

    public long AsInt => IsNull ? throw new InvalidOperationException("value is NULL") : _intValue;
    public decimal AsDecimal => IsNull ? throw new InvalidOperationException("value is NULL") : _decimalValue;
    public string AsText => IsNull ? throw new InvalidOperationException("value is NULL") : _textValue!;
    public DateTime AsDate => IsNull ? throw new InvalidOperationException("value is NULL") : _dateValue;
    public bool AsBool => IsNull ? throw new InvalidOperationException("value is NULL") : _boolValue;

    /// <summary>Numeric value regardless of Int/Decimal storage, for cross-numeric-type comparison.</summary>
    private decimal NumericValue => Type == SqlType.Int ? _intValue : _decimalValue;

    private static bool IsNumeric(SqlType type) => type is SqlType.Int or SqlType.Decimal;

    /// <summary>Three-valued equality: NULL = anything (including NULL) is Unknown.</summary>
    public SqlBool EqualsSql(SqlValue other)
    {
        if (IsNull || other.IsNull) return SqlBool.Unknown;
        return SqlBoolExtensions.FromBool(RawEquals(other));
    }

    public SqlBool NotEqualsSql(SqlValue other)
    {
        if (IsNull || other.IsNull) return SqlBool.Unknown;
        return SqlBoolExtensions.FromBool(!RawEquals(other));
    }

    public SqlBool LessThan(SqlValue other) => Compare(other, cmp => cmp < 0);
    public SqlBool LessThanOrEqual(SqlValue other) => Compare(other, cmp => cmp <= 0);
    public SqlBool GreaterThan(SqlValue other) => Compare(other, cmp => cmp > 0);
    public SqlBool GreaterThanOrEqual(SqlValue other) => Compare(other, cmp => cmp >= 0);

    public SqlBool IsNullSql() => SqlBoolExtensions.FromBool(IsNull);
    public SqlBool IsNotNullSql() => SqlBoolExtensions.FromBool(!IsNull);

    private SqlBool Compare(SqlValue other, Func<int, bool> predicate)
    {
        if (IsNull || other.IsNull) return SqlBool.Unknown;
        return SqlBoolExtensions.FromBool(predicate(RawCompare(other)));
    }

    private bool RawEquals(SqlValue other)
    {
        if (IsNumeric(Type) && IsNumeric(other.Type)) return NumericValue == other.NumericValue;
        RequireSameType(other);
        return Type switch
        {
            SqlType.Text => string.Equals(_textValue, other._textValue, StringComparison.Ordinal),
            SqlType.Date => _dateValue == other._dateValue,
            SqlType.Bool => _boolValue == other._boolValue,
            _ => throw new InvalidOperationException($"unreachable: {Type}")
        };
    }

    private int RawCompare(SqlValue other)
    {
        if (IsNumeric(Type) && IsNumeric(other.Type)) return NumericValue.CompareTo(other.NumericValue);
        RequireSameType(other);
        return Type switch
        {
            SqlType.Text => string.CompareOrdinal(_textValue, other._textValue),
            SqlType.Date => _dateValue.CompareTo(other._dateValue),
            SqlType.Bool => _boolValue.CompareTo(other._boolValue),
            _ => throw new InvalidOperationException($"unreachable: {Type}")
        };
    }

    private void RequireSameType(SqlValue other)
    {
        if (Type != other.Type)
            throw new InvalidOperationException($"cannot compare {Type} with {other.Type}");
    }

    /// <summary>
    /// Total order for index keys and ORDER BY: NULLs sort last, regardless of ASC/DESC
    /// (the operator that consumes this reverses non-null order for DESC but keeps NULLs last).
    /// </summary>
    public int CompareTo(SqlValue other)
    {
        if (IsNull && other.IsNull) return 0;
        if (IsNull) return 1;
        if (other.IsNull) return -1;
        return RawCompare(other);
    }

    public bool Equals(SqlValue other)
    {
        if (IsNull || other.IsNull) return IsNull && other.IsNull && Type == other.Type;
        return Type == other.Type ? RawEquals(other) : IsNumeric(Type) && IsNumeric(other.Type) && RawEquals(other);
    }

    public override bool Equals(object? obj) => obj is SqlValue other && Equals(other);

    public override int GetHashCode()
    {
        if (IsNull) return HashCode.Combine(Type, "NULL");
        return Type switch
        {
            // Int and Decimal must hash identically for equal values (Equals treats Int(5) and
            // Decimal(5.0) as equal via NumericValue) — both funnel through the same decimal
            // conversion here so that invariant holds. Hashing _intValue and _decimalValue
            // separately would violate the Equals/GetHashCode contract: `long.GetHashCode()` and
            // `decimal.GetHashCode()` disagree for negative values even when the values are
            // numerically equal (e.g. -3L vs -3.0m), which would let equal keys land in
            // different Dictionary/HashSet buckets (HashIndex, HashJoin's build side).
            SqlType.Int or SqlType.Decimal => NumericValue.GetHashCode(),
            SqlType.Text => _textValue!.GetHashCode(StringComparison.Ordinal),
            SqlType.Date => _dateValue.GetHashCode(),
            SqlType.Bool => _boolValue.GetHashCode(),
            _ => 0
        };
    }

    public override string ToString()
    {
        if (IsNull) return "NULL";
        return Type switch
        {
            SqlType.Int => _intValue.ToString(CultureInfo.InvariantCulture),
            SqlType.Decimal => _decimalValue.ToString(CultureInfo.InvariantCulture),
            SqlType.Text => _textValue!,
            SqlType.Date => _dateValue.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            SqlType.Bool => _boolValue ? "true" : "false",
            _ => "?"
        };
    }
}
