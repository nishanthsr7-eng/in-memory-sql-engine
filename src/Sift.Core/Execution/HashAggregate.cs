using Sift.Core.Catalog;
using Sift.Core.Planning;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;

namespace Sift.Core.Execution;

public sealed record AggregateSpec(AggregateFunc Func, int ArgumentColumnIndex, bool IsCountStar, string OutputName, SqlType OutputType);

/// <summary>
/// GROUP BY + aggregates, computed with one hash-table pass: group key → running accumulators
/// per aggregate. NULLs are excluded from every aggregate but COUNT(*), per SQL semantics
/// (PLAN.md §8). A GROUP BY with zero input groups produces zero rows; a bare aggregate with no
/// GROUP BY over zero rows still produces exactly one row (e.g. COUNT(*) = 0).
/// </summary>
public sealed class HashAggregate : Operator
{
    private readonly Operator _child;
    private readonly int[] _groupByIndices;
    private readonly AggregateSpec[] _specs;
    private readonly Schema _outputSchema;

    public HashAggregate(Operator child, int[] groupByIndices, AggregateSpec[] specs)
    {
        _child = child;
        _groupByIndices = groupByIndices;
        _specs = specs;

        var childColumns = child.OutputSchema.Columns;
        var outputColumns = new Column[groupByIndices.Length + specs.Length];
        for (var i = 0; i < groupByIndices.Length; i++) outputColumns[i] = childColumns[groupByIndices[i]];
        for (var i = 0; i < specs.Length; i++) outputColumns[groupByIndices.Length + i] = new Column(specs[i].OutputName, specs[i].OutputType);
        _outputSchema = new Schema(outputColumns);

        EstimatedRowCount = groupByIndices.Length == 0
            ? 1
            : Math.Max(1, child.EstimatedRowCount * CostModel.DefaultGroupingFraction);
        EstimatedCost = child.EstimatedCost + child.EstimatedRowCount * CostModel.HashAggregateCostPerRow;
    }

    public override Schema OutputSchema => _outputSchema;

    public override IEnumerable<Row> Execute()
    {
        var groups = new Dictionary<GroupKey, IAccumulator[]>();
        var order = new List<GroupKey>(); // preserves first-seen order for deterministic output

        foreach (var row in _child.Execute())
        {
            var keyValues = new SqlValue[_groupByIndices.Length];
            for (var i = 0; i < _groupByIndices.Length; i++) keyValues[i] = row[_groupByIndices[i]];
            var key = new GroupKey(keyValues);

            if (!groups.TryGetValue(key, out var accumulators))
            {
                accumulators = _specs.Select(CreateAccumulator).ToArray();
                groups[key] = accumulators;
                order.Add(key);
            }

            for (var i = 0; i < _specs.Length; i++)
            {
                var spec = _specs[i];
                accumulators[i].Add(spec.IsCountStar ? default : row[spec.ArgumentColumnIndex]);
            }
        }

        if (_groupByIndices.Length == 0 && groups.Count == 0)
        {
            yield return BuildRow(Array.Empty<SqlValue>(), _specs.Select(CreateAccumulator).ToArray());
            yield break;
        }

        foreach (var key in order)
            yield return BuildRow(key.Values, groups[key]);
    }

    private Row BuildRow(SqlValue[] groupValues, IAccumulator[] accumulators)
    {
        var values = new SqlValue[groupValues.Length + accumulators.Length];
        Array.Copy(groupValues, values, groupValues.Length);
        for (var i = 0; i < accumulators.Length; i++) values[groupValues.Length + i] = accumulators[i].Result();
        return new Row(values);
    }

    private static IAccumulator CreateAccumulator(AggregateSpec spec) => spec.Func switch
    {
        AggregateFunc.Count => new CountAccumulator(spec.IsCountStar),
        AggregateFunc.Sum => new SumAccumulator(),
        AggregateFunc.Avg => new AvgAccumulator(),
        AggregateFunc.Min => new MinMaxAccumulator(wantMin: true, spec.OutputType),
        AggregateFunc.Max => new MinMaxAccumulator(wantMin: false, spec.OutputType),
        _ => throw new InvalidOperationException($"unreachable: {spec.Func}")
    };

    public override string Explain(int indent)
    {
        var aggs = string.Join(", ", _specs.Select(s => s.OutputName));
        var groupBy = _groupByIndices.Length == 0 ? "" : $" GROUP BY ({string.Join(", ", _groupByIndices.Select(i => _child.OutputSchema.Columns[i].Name))})";
        return $"{Ind(indent)}HashAggregate ({aggs}){groupBy}{CostSuffix()}\n{_child.Explain(indent + 1)}";
    }

    private interface IAccumulator
    {
        void Add(SqlValue value);
        SqlValue Result();
    }

    private sealed class CountAccumulator : IAccumulator
    {
        private readonly bool _countAll;
        private long _count;
        public CountAccumulator(bool countAll) => _countAll = countAll;
        public void Add(SqlValue value) { if (_countAll || !value.IsNull) _count++; }
        public SqlValue Result() => SqlValue.Int(_count);
    }

    private sealed class SumAccumulator : IAccumulator
    {
        private decimal _sum;
        private bool _any;
        public void Add(SqlValue value)
        {
            if (value.IsNull) return;
            _sum += value.Type == SqlType.Int ? value.AsInt : value.AsDecimal;
            _any = true;
        }
        public SqlValue Result() => _any ? SqlValue.Decimal(_sum) : SqlValue.Null(SqlType.Decimal);
    }

    private sealed class AvgAccumulator : IAccumulator
    {
        private decimal _sum;
        private long _count;
        public void Add(SqlValue value)
        {
            if (value.IsNull) return;
            _sum += value.Type == SqlType.Int ? value.AsInt : value.AsDecimal;
            _count++;
        }
        public SqlValue Result() => _count > 0 ? SqlValue.Decimal(_sum / _count) : SqlValue.Null(SqlType.Decimal);
    }

    private sealed class MinMaxAccumulator : IAccumulator
    {
        private readonly bool _wantMin;
        private readonly SqlType _outputType;
        private SqlValue _current;
        private bool _any;
        public MinMaxAccumulator(bool wantMin, SqlType outputType) { _wantMin = wantMin; _outputType = outputType; }
        public void Add(SqlValue value)
        {
            if (value.IsNull) return;
            if (!_any || (_wantMin ? value.CompareTo(_current) < 0 : value.CompareTo(_current) > 0)) _current = value;
            _any = true;
        }
        public SqlValue Result() => _any ? _current : SqlValue.Null(_outputType);
    }

    private readonly struct GroupKey : IEquatable<GroupKey>
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
}
