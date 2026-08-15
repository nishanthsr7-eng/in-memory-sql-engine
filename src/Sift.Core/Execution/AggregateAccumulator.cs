using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Execution;

/// <summary>
/// Per-group running state for one aggregate. <see cref="MergeFrom"/> exists only for parallel
/// execution (<see cref="ParallelHashAggregate"/>): each partition accumulates independently
/// into its own instance, then partitions are combined single-threaded by merging same-key
/// accumulators pairwise — never by touching another partition's state directly.
/// </summary>
internal interface IAccumulator
{
    void Add(SqlValue value);
    void MergeFrom(IAccumulator other);
    SqlValue Result();
}

internal static class AccumulatorFactory
{
    public static IAccumulator Create(AggregateSpec spec) => spec.Func switch
    {
        AggregateFunc.Count => new CountAccumulator(spec.IsCountStar),
        AggregateFunc.Sum => new SumAccumulator(),
        AggregateFunc.Avg => new AvgAccumulator(),
        AggregateFunc.Min => new MinMaxAccumulator(wantMin: true, spec.OutputType),
        AggregateFunc.Max => new MinMaxAccumulator(wantMin: false, spec.OutputType),
        _ => throw new InvalidOperationException($"unreachable: {spec.Func}")
    };
}

internal sealed class CountAccumulator : IAccumulator
{
    private readonly bool _countAll;
    private long _count;
    public CountAccumulator(bool countAll) => _countAll = countAll;
    public void Add(SqlValue value) { if (_countAll || !value.IsNull) _count++; }
    public void MergeFrom(IAccumulator other) => _count += ((CountAccumulator)other)._count;
    public SqlValue Result() => SqlValue.Int(_count);
}

internal sealed class SumAccumulator : IAccumulator
{
    private decimal _sum;
    private bool _any;
    public void Add(SqlValue value)
    {
        if (value.IsNull) return;
        _sum += value.Type == SqlType.Int ? value.AsInt : value.AsDecimal;
        _any = true;
    }
    public void MergeFrom(IAccumulator other)
    {
        var o = (SumAccumulator)other;
        _sum += o._sum;
        _any |= o._any;
    }
    public SqlValue Result() => _any ? SqlValue.Decimal(_sum) : SqlValue.Null(SqlType.Decimal);
}

internal sealed class AvgAccumulator : IAccumulator
{
    private decimal _sum;
    private long _count;
    public void Add(SqlValue value)
    {
        if (value.IsNull) return;
        _sum += value.Type == SqlType.Int ? value.AsInt : value.AsDecimal;
        _count++;
    }
    public void MergeFrom(IAccumulator other)
    {
        var o = (AvgAccumulator)other;
        _sum += o._sum;
        _count += o._count;
    }
    public SqlValue Result() => _count > 0 ? SqlValue.Decimal(_sum / _count) : SqlValue.Null(SqlType.Decimal);
}

internal sealed class MinMaxAccumulator : IAccumulator
{
    private readonly bool _wantMin;
    private readonly SqlType _outputType;
    private SqlValue _current;
    private bool _any;
    public MinMaxAccumulator(bool wantMin, SqlType outputType) { _wantMin = wantMin; _outputType = outputType; }
    public void Add(SqlValue value)
    {
        if (value.IsNull) return;
        if (!_any || Better(value, _current)) _current = value;
        _any = true;
    }
    public void MergeFrom(IAccumulator other)
    {
        var o = (MinMaxAccumulator)other;
        if (!o._any) return;
        if (!_any || Better(o._current, _current)) _current = o._current;
        _any = true;
    }
    private bool Better(SqlValue candidate, SqlValue current) =>
        _wantMin ? candidate.CompareTo(current) < 0 : candidate.CompareTo(current) > 0;
    public SqlValue Result() => _any ? _current : SqlValue.Null(_outputType);
}

/// <summary>A GROUP BY key: the tuple of group-by column values for one output row.</summary>
internal readonly struct GroupKey : IEquatable<GroupKey>
{
    public readonly SqlValue[] Values;
    public GroupKey(SqlValue[] values) => Values = values;

    public bool Equals(GroupKey other)
    {
        if (Values.Length != other.Values.Length) return false;
        for (var i = 0; i < Values.Length; i++)
            if (!Values[i].Equals(other.Values[i])) return false;
        return true;
    }

    public override bool Equals(object? obj) => obj is GroupKey other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (var v in Values) hash.Add(v);
        return hash.ToHashCode();
    }
}
