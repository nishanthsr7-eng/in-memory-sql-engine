using Sift.Core.Catalog;
using Sift.Core.Values;

namespace Sift.Core.Execution;

/// <summary>
/// O(n·m) equi-join: materializes the right (inner) side once, then rescans it per left row.
/// Wins over HashJoin only when one side is tiny — kept mainly as the benchmark baseline that
/// makes HashJoin's O(n+m) actually mean something (PLAN.md §9).
/// </summary>
public sealed class NestedLoopJoin : Operator
{
    private readonly Operator _left;
    private readonly Operator _right;
    private readonly int _leftColumnIndex;
    private readonly int _rightColumnIndex;
    private readonly Schema _outputSchema;

    public NestedLoopJoin(Operator left, Operator right, int leftColumnIndex, int rightColumnIndex)
    {
        _left = left;
        _right = right;
        _leftColumnIndex = leftColumnIndex;
        _rightColumnIndex = rightColumnIndex;
        _outputSchema = JoinSupport.CombineSchemas(left.OutputSchema, right.OutputSchema);
    }

    public override Schema OutputSchema => _outputSchema;

    public override IEnumerable<Row> Execute()
    {
        var rightRows = _right.Execute().ToList();

        foreach (var leftRow in _left.Execute())
        {
            var leftKey = leftRow[_leftColumnIndex];
            if (leftKey.IsNull) continue; // NULL never equi-joins (three-valued equality)

            foreach (var rightRow in rightRows)
                if (leftKey.EqualsSql(rightRow[_rightColumnIndex]).IsTrue())
                    yield return JoinSupport.Combine(leftRow, rightRow);
        }
    }

    public override string Explain(int indent) =>
        $"{Ind(indent)}NestedLoopJoin\n{_left.Explain(indent + 1)}\n{_right.Explain(indent + 1)}";
}
