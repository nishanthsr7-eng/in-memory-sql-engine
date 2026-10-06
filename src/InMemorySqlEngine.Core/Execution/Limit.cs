using InMemorySqlEngine.Core.Catalog;

namespace InMemorySqlEngine.Core.Execution;

public sealed class Limit : Operator
{
    private readonly Operator _child;
    private readonly int _count;

    public Limit(Operator child, int count)
    {
        _child = child;
        _count = count;
        // Conservative upper bound: real (lazy) execution often does less work than this once
        // the count is reached, but a static estimate can't see that short-circuit.
        EstimatedRowCount = Math.Min(child.EstimatedRowCount, Math.Max(count, 0));
        EstimatedCost = child.EstimatedCost;
    }

    public override Schema OutputSchema => _child.OutputSchema;

    public override IEnumerable<Row> Execute()
    {
        if (_count <= 0) yield break;

        var returned = 0;
        foreach (var row in _child.Execute())
        {
            yield return row;
            if (++returned >= _count) yield break; // stops pulling from _child — nothing further downstream runs
        }
    }

    public override string Explain(int indent) => $"{Ind(indent)}Limit {_count}{CostSuffix()}\n{_child.Explain(indent + 1)}";
}
