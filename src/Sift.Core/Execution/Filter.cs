using Sift.Core.Catalog;
using Sift.Core.Planning;
using Sift.Core.Values;

namespace Sift.Core.Execution;

public sealed class Filter : Operator
{
    private readonly Operator _child;
    private readonly IPredicate _predicate;

    public Filter(Operator child, IPredicate predicate)
    {
        _child = child;
        _predicate = predicate;
        // No per-predicate selectivity estimate here — only the chosen index conjunct (Phase 3)
        // gets a real one; everything else, including HAVING, uses the flat default.
        EstimatedRowCount = child.EstimatedRowCount * CostModel.DefaultFilterSelectivity;
        EstimatedCost = child.EstimatedCost + child.EstimatedRowCount * CostModel.FilterCostPerRow;
    }

    public override Schema OutputSchema => _child.OutputSchema;

    public override IEnumerable<Row> Execute()
    {
        foreach (var row in _child.Execute())
            if (_predicate.Evaluate(row).IsTrue()) // WHERE/HAVING keep TRUE rows only — UNKNOWN is filtered out
                yield return row;
    }

    public override string Explain(int indent) => $"{Ind(indent)}Filter{CostSuffix()}\n{_child.Explain(indent + 1)}";
}
