using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Planning;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

/// <summary>
/// Fused parallel scan + filter + partial aggregate (docs/design.md §8). The table is split into
/// contiguous row-range partitions, one per worker; each partition builds its own local
/// per-group accumulator dictionary with no shared mutable state and no locks, so there's
/// nothing to synchronize until every partition has finished. The merge step — combining
/// same-key accumulators pairwise across partitions — then runs single-threaded, since it's
/// cheap relative to the scan (partition count, not row count) and correctness is easier to
/// reason about without a second layer of concurrent access.
///
/// Deliberately bypasses the composable Operator tree: partitioning only makes sense starting
/// from a raw, random-access Table, not an arbitrary upstream `IEnumerable&lt;Row&gt;` that may
/// itself be lazy, stateful, or expensive to enumerate more than once.
/// </summary>
public sealed class ParallelHashAggregate : Operator
{
    private readonly Table _table;
    private readonly IPredicate? _filter;
    private readonly int[] _groupByIndices;
    private readonly AggregateSpec[] _specs;
    private readonly Schema _outputSchema;
    private readonly int _degreeOfParallelism;

    public ParallelHashAggregate(Table table, IPredicate? filter, int[] groupByIndices, AggregateSpec[] specs, int degreeOfParallelism)
    {
        _table = table;
        _filter = filter;
        _groupByIndices = groupByIndices;
        _specs = specs;
        _degreeOfParallelism = Math.Max(1, degreeOfParallelism);

        var sourceColumns = table.Schema.Columns;
        var outputColumns = new Column[groupByIndices.Length + specs.Length];
        for (var i = 0; i < groupByIndices.Length; i++) outputColumns[i] = sourceColumns[groupByIndices[i]];
        for (var i = 0; i < specs.Length; i++) outputColumns[groupByIndices.Length + i] = new Column(specs[i].OutputName, specs[i].OutputType);
        _outputSchema = new Schema(outputColumns);

        EstimatedRowCount = groupByIndices.Length == 0 ? 1 : Math.Max(1, table.RowCount * CostModel.DefaultGroupingFraction);
        EstimatedCost = (CostModel.SeqScanCost(table.RowCount) + table.RowCount * CostModel.HashAggregateCostPerRow) / _degreeOfParallelism;
    }

    public override Schema OutputSchema => _outputSchema;

    public override IEnumerable<Row> Execute()
    {
        var partitions = Partition(_table.RowCount, _degreeOfParallelism);
        var localResults = new Dictionary<GroupKey, IAccumulator[]>[partitions.Count];

        // Each iteration writes only to its own localResults[p] slot — no shared state between
        // threads, so nothing here needs a lock.
        Parallel.For(0, partitions.Count, new ParallelOptions { MaxDegreeOfParallelism = _degreeOfParallelism }, p =>
        {
            var (start, end) = partitions[p];
            var local = new Dictionary<GroupKey, IAccumulator[]>();

            for (var i = start; i < end; i++)
            {
                var row = _table.GetRow(i);
                if (_filter is not null && !_filter.Evaluate(row).IsTrue()) continue;

                var keyValues = new SqlValue[_groupByIndices.Length];
                for (var g = 0; g < _groupByIndices.Length; g++) keyValues[g] = row[_groupByIndices[g]];
                var key = new GroupKey(keyValues);

                if (!local.TryGetValue(key, out var accumulators))
                {
                    accumulators = new IAccumulator[_specs.Length];
                    for (var s = 0; s < _specs.Length; s++) accumulators[s] = AccumulatorFactory.Create(_specs[s]);
                    local[key] = accumulators;
                }

                for (var s = 0; s < _specs.Length; s++)
                {
                    var spec = _specs[s];
                    accumulators[s].Add(spec.IsCountStar ? default : row[spec.ArgumentColumnIndex]);
                }
            }

            localResults[p] = local;
        });

        var merged = new Dictionary<GroupKey, IAccumulator[]>();
        var order = new List<GroupKey>(); // preserves first-partition-first-seen order for deterministic output
        foreach (var local in localResults)
        {
            foreach (var (key, accumulators) in local)
            {
                if (!merged.TryGetValue(key, out var target))
                {
                    merged[key] = accumulators;
                    order.Add(key);
                }
                else
                {
                    for (var s = 0; s < target.Length; s++) target[s].MergeFrom(accumulators[s]);
                }
            }
        }

        if (_groupByIndices.Length == 0 && merged.Count == 0)
        {
            var empty = new IAccumulator[_specs.Length];
            for (var s = 0; s < _specs.Length; s++) empty[s] = AccumulatorFactory.Create(_specs[s]);
            yield return HashAggregate.BuildRow(Array.Empty<SqlValue>(), empty);
            yield break;
        }

        foreach (var key in order)
            yield return HashAggregate.BuildRow(key.Values, merged[key]);
    }

    private static List<(int Start, int End)> Partition(int rowCount, int parts)
    {
        var result = new List<(int Start, int End)>();
        if (rowCount == 0) return result;

        var chunkSize = (rowCount + parts - 1) / parts;
        for (var p = 0; p < parts; p++)
        {
            var start = p * chunkSize;
            if (start >= rowCount) break;
            result.Add((start, Math.Min(start + chunkSize, rowCount)));
        }
        return result;
    }

    public override string Explain(int indent) =>
        $"{Ind(indent)}ParallelHashAggregate (dop={_degreeOfParallelism}){CostSuffix()}\n{Ind(indent + 1)}SeqScan on {_table.Name}  (est. rows={_table.RowCount})";
}
