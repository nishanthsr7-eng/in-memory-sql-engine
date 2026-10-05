using Sift.Core.Catalog;
using Sift.Core.Planning;
using Sift.Core.Values;

namespace Sift.Core.Execution;

/// <summary>
/// O(n+m) equi-join: builds a hash table on the right side, then probes it once per left row.
/// Always builds on the right — join reordering (picking the smaller side to build) is out of
/// scope (docs/design.md §1); callers should put the smaller table on the right of JOIN.
/// </summary>
public sealed class HashJoin : Operator
{
    private readonly Operator _left;
    private readonly Operator _right;
    private readonly int _leftColumnIndex;
    private readonly int _rightColumnIndex;
    private readonly Schema _outputSchema;

    public HashJoin(Operator left, Operator right, int leftColumnIndex, int rightColumnIndex, double? estimatedRowCount = null)
    {
        _left = left;
        _right = right;
        _leftColumnIndex = leftColumnIndex;
        _rightColumnIndex = rightColumnIndex;
        _outputSchema = JoinSupport.CombineSchemas(left.OutputSchema, right.OutputSchema);
        EstimatedRowCount = estimatedRowCount ?? Math.Max(left.EstimatedRowCount, right.EstimatedRowCount);
        EstimatedCost = left.EstimatedCost + right.EstimatedCost + CostModel.HashJoinCost(left.EstimatedRowCount, right.EstimatedRowCount);
    }

    public override Schema OutputSchema => _outputSchema;

    public override IEnumerable<Row> Execute()
    {
        var buildTable = new Dictionary<SqlValue, List<Row>>();
        foreach (var rightRow in _right.Execute())
        {
            var key = rightRow[_rightColumnIndex];
            if (key.IsNull) continue;
            if (!buildTable.TryGetValue(key, out var bucket)) buildTable[key] = bucket = new List<Row>();
            bucket.Add(rightRow);
        }

        foreach (var leftRow in _left.Execute())
        {
            var key = leftRow[_leftColumnIndex];
            if (key.IsNull) continue;
            if (!buildTable.TryGetValue(key, out var matches)) continue;
            foreach (var rightRow in matches)
                yield return JoinSupport.Combine(leftRow, rightRow);
        }
    }

    public override string Explain(int indent) =>
        $"{Ind(indent)}HashJoin{CostSuffix()}\n{_left.Explain(indent + 1)}\n{_right.Explain(indent + 1)}";
}
