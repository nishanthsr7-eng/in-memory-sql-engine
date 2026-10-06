using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Indexing;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

public abstract record IndexCondition
{
    public sealed record Exact(SqlValue Key) : IndexCondition;

    public sealed record Range(SqlValue? Lo, bool LoInclusive, SqlValue? Hi, bool HiInclusive) : IndexCondition;
}

/// <summary>Answers one predicate straight from an <see cref="IIndex"/> instead of a full scan.</summary>
public sealed class IndexScan : Operator
{
    private readonly Table _table;
    private readonly IIndex _index;
    private readonly IndexCondition _condition;

    public IndexScan(Table table, IIndex index, IndexCondition condition, double estimatedRowCount, double estimatedCost)
    {
        _table = table;
        _index = index;
        _condition = condition;
        EstimatedRowCount = estimatedRowCount;
        EstimatedCost = estimatedCost;
    }

    public override Schema OutputSchema => _table.Schema;

    public override IEnumerable<Row> Execute()
    {
        var rowIds = _condition switch
        {
            IndexCondition.Exact eq => _index.Lookup(eq.Key),
            IndexCondition.Range range => _index.Range(range.Lo, range.LoInclusive, range.Hi, range.HiInclusive),
            _ => throw new InvalidOperationException($"unreachable: {_condition}")
        };
        foreach (var rowId in rowIds) yield return _table.GetRow(rowId);
    }

    public override string Explain(int indent)
    {
        var desc = _condition switch
        {
            IndexCondition.Exact eq => $"{_index.ColumnName} = {eq.Key}",
            IndexCondition.Range r => $"{r.Lo?.ToString() ?? "-inf"} <= {_index.ColumnName} <= {r.Hi?.ToString() ?? "+inf"}",
            _ => "?"
        };
        return $"{Ind(indent)}IndexScan on {_table.Name} using {_index.Kind} index on {desc}{CostSuffix()}";
    }
}
