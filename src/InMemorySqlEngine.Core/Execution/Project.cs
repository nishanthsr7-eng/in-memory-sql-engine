using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Planning;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

/// <summary>Reorders/renames/subsets columns by index — the mapping is resolved once, at plan time.</summary>
public sealed class Project : Operator
{
    private readonly Operator _child;
    private readonly Schema _outputSchema;
    private readonly int[] _sourceIndices;

    public Project(Operator child, Schema outputSchema, int[] sourceIndices)
    {
        _child = child;
        _outputSchema = outputSchema;
        _sourceIndices = sourceIndices;
        EstimatedRowCount = child.EstimatedRowCount; // projection never changes row count
        EstimatedCost = child.EstimatedCost + child.EstimatedRowCount * CostModel.ProjectCostPerRow;
    }

    public override Schema OutputSchema => _outputSchema;

    public override IEnumerable<Row> Execute()
    {
        foreach (var row in _child.Execute())
        {
            var values = new SqlValue[_sourceIndices.Length];
            for (var i = 0; i < _sourceIndices.Length; i++) values[i] = row[_sourceIndices[i]];
            yield return new Row(values);
        }
    }

    public override string Explain(int indent) =>
        $"{Ind(indent)}Project ({string.Join(", ", _outputSchema.Columns.Select(c => c.Name))}){CostSuffix()}\n{_child.Explain(indent + 1)}";
}
