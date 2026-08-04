using Sift.Core.Catalog;

namespace Sift.Core.Execution;

/// <summary>Full table scan — the always-available fallback every planner decision is compared against.</summary>
public sealed class SeqScan : Operator
{
    private readonly Table _table;

    public SeqScan(Table table) => _table = table;

    public override Schema OutputSchema => _table.Schema;

    public override IEnumerable<Row> Execute() => _table.Rows;

    public override string Explain(int indent) =>
        $"{Ind(indent)}SeqScan on {_table.Name}  (est. rows={_table.RowCount})";
}
