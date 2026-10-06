using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Planning;
using InMemorySqlEngine.Core.Sql.Ast;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

public sealed record AggregateSpec(AggregateFunc Func, int ArgumentColumnIndex, bool IsCountStar, string OutputName, SqlType OutputType);

/// <summary>
/// GROUP BY + aggregates, computed with one hash-table pass: group key → running accumulators
/// per aggregate. NULLs are excluded from every aggregate but COUNT(*), per SQL semantics
/// (docs/design.md §6). A GROUP BY with zero input groups produces zero rows; a bare aggregate with no
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
                accumulators = _specs.Select(AccumulatorFactory.Create).ToArray();
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
            yield return BuildRow(Array.Empty<SqlValue>(), _specs.Select(AccumulatorFactory.Create).ToArray());
            yield break;
        }

        foreach (var key in order)
            yield return BuildRow(key.Values, groups[key]);
    }

    internal static Row BuildRow(SqlValue[] groupValues, IAccumulator[] accumulators)
    {
        var values = new SqlValue[groupValues.Length + accumulators.Length];
        Array.Copy(groupValues, values, groupValues.Length);
        for (var i = 0; i < accumulators.Length; i++) values[groupValues.Length + i] = accumulators[i].Result();
        return new Row(values);
    }

    public override string Explain(int indent)
    {
        var aggs = string.Join(", ", _specs.Select(s => s.OutputName));
        var groupBy = _groupByIndices.Length == 0 ? "" : $" GROUP BY ({string.Join(", ", _groupByIndices.Select(i => _child.OutputSchema.Columns[i].Name))})";
        return $"{Ind(indent)}HashAggregate ({aggs}){groupBy}{CostSuffix()}\n{_child.Explain(indent + 1)}";
    }
}
