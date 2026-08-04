using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Sql;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Execution;

public class OperatorTests
{
    /// <summary>Counts how many rows its child actually yielded — the "rows touched" probe.</summary>
    private sealed class CountingOperator : Operator
    {
        private readonly Operator _child;
        public int TouchCount;
        public CountingOperator(Operator child) => _child = child;
        public override Schema OutputSchema => _child.OutputSchema;
        public override IEnumerable<Row> Execute()
        {
            foreach (var row in _child.Execute()) { TouchCount++; yield return row; }
        }
        public override string Explain(int indent) => _child.Explain(indent);
    }

    private static Table BuildLargeTable(int rowCount)
    {
        var schema = new Schema(new[]
        {
            new Column("id", SqlType.Int),
            new Column("even", SqlType.Bool),
        });
        var rows = new Row[rowCount];
        for (var i = 0; i < rowCount; i++)
            rows[i] = new Row(new[] { SqlValue.Int(i), SqlValue.Bool(i % 2 == 0) });
        return new Table("big", schema, rows);
    }

    [Fact]
    public void LimitOverFilter_DoesNotTouchAllRows()
    {
        var table = BuildLargeTable(1000);
        var counting = new CountingOperator(new SeqScan(table));
        var predicate = new ExprPredicate(
            new ComparisonExpr(new ColumnRefExpr(null, "even"), ComparisonOp.Eq, new LiteralExpr(SqlValue.Bool(true))),
            counting.OutputSchema);
        var plan = new Limit(new Filter(counting, predicate), 10);

        var result = plan.Execute().ToList();

        Assert.Equal(10, result.Count);
        Assert.True(counting.TouchCount < 1000, $"expected early termination, touched {counting.TouchCount}/1000 rows");
        Assert.True(counting.TouchCount >= 10);
    }

    [Fact]
    public void LimitOverAggregateAndSort_MustTouchEveryRow()
    {
        // Aggregate and Sort are blocking: they can't know they've seen the whole group/order
        // until the child is exhausted, so a Limit on top can't shrink what they pull from below
        // it — unlike Filter, which can short-circuit as soon as enough matches are found.
        var table = BuildLargeTable(1000);
        var counting = new CountingOperator(new SeqScan(table));
        var aggregate = new HashAggregate(counting, groupByIndices: new[] { 1 },
            specs: new[] { new AggregateSpec(AggregateFunc.Count, ArgumentColumnIndex: -1, IsCountStar: true, "COUNT(*)", SqlType.Int) });
        var sort = new Sort(aggregate, new[] { new SortKey(0, Descending: false) });
        var plan = new Limit(sort, 1);

        _ = plan.Execute().ToList();

        Assert.Equal(1000, counting.TouchCount);
    }

    private static Sift.Core.Catalog.Catalog BuildJoinCatalog()
    {
        var salesSchema = new Schema(new[]
        {
            new Column("region", SqlType.Text),
            new Column("brand", SqlType.Text),
            new Column("units_sold", SqlType.Int),
        });
        var salesRows = new[]
        {
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("MiBrand1"), SqlValue.Int(10) }),
            new Row(new[] { SqlValue.Text("PL-South"), SqlValue.Text("MiBrand1"), SqlValue.Int(20) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("YoBrand1"), SqlValue.Int(30) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("MiBrand1"), SqlValue.Null(SqlType.Int) }),
            new Row(new[] { SqlValue.Text("PL-North"), SqlValue.Text("Unknown"), SqlValue.Int(5) }), // no matching brand
        };

        var brandsSchema = new Schema(new[]
        {
            new Column("brand", SqlType.Text),
            new Column("category", SqlType.Text),
        });
        var brandsRows = new[]
        {
            new Row(new[] { SqlValue.Text("MiBrand1"), SqlValue.Text("Milk") }),
            new Row(new[] { SqlValue.Text("YoBrand1"), SqlValue.Text("Yogurt") }),
        };

        var catalog = new Sift.Core.Catalog.Catalog();
        catalog.AddTable(new Table("sales", salesSchema, salesRows));
        catalog.AddTable(new Table("brands", brandsSchema, brandsRows));
        return catalog;
    }

    [Fact]
    public void HashJoin_MatchesEquiJoinKeysAndDropsUnmatched()
    {
        var catalog = BuildJoinCatalog();
        var left = new SeqScan(catalog.GetTable("sales"));
        var right = new SeqScan(catalog.GetTable("brands"));
        var join = new HashJoin(left, right, leftColumnIndex: 1, rightColumnIndex: 0);

        var rows = join.Execute().ToList();

        Assert.Equal(4, rows.Count); // 5 sales rows, 1 has no matching brand ("Unknown")
        Assert.Equal(5, join.OutputSchema.Columns.Count); // region, brand, units_sold, brand, category
    }

    [Fact]
    public void NestedLoopJoin_AgreesWithHashJoin()
    {
        var catalog = BuildJoinCatalog();

        Operator BuildLeft() => new SeqScan(catalog.GetTable("sales"));
        Operator BuildRight() => new SeqScan(catalog.GetTable("brands"));

        var hashRows = new HashJoin(BuildLeft(), BuildRight(), 1, 0).Execute()
            .Select(r => r.Values.Select(v => v.ToString())).ToList();
        var loopRows = new NestedLoopJoin(BuildLeft(), BuildRight(), 1, 0).Execute()
            .Select(r => r.Values.Select(v => v.ToString())).ToList();

        Assert.Equal(hashRows.Count, loopRows.Count);
        for (var i = 0; i < hashRows.Count; i++)
            Assert.Equal(hashRows[i], loopRows[i]);
    }

    [Fact]
    public void HashAggregate_GroupsAndSkipsNulls()
    {
        var catalog = BuildJoinCatalog();
        var scan = new SeqScan(catalog.GetTable("sales"));
        var specs = new[]
        {
            new AggregateSpec(AggregateFunc.Sum, ArgumentColumnIndex: 2, IsCountStar: false, "SUM(units_sold)", SqlType.Decimal),
            new AggregateSpec(AggregateFunc.Count, ArgumentColumnIndex: -1, IsCountStar: true, "COUNT(*)", SqlType.Int),
        };
        var aggregate = new HashAggregate(scan, groupByIndices: new[] { 1 }, specs);

        var rows = aggregate.Execute().ToDictionary(r => r[0].AsText, r => r);

        // MiBrand1: rows are 10, 20, NULL -> SUM skips the NULL, COUNT(*) still counts it
        Assert.Equal(30m, rows["MiBrand1"][1].AsDecimal);
        Assert.Equal(3L, rows["MiBrand1"][2].AsInt);
    }

    [Fact]
    public void HashAggregate_NoGroupByOverEmptyInput_ProducesOneRow()
    {
        var emptyTable = new Table("empty", new Schema(new[] { new Column("x", SqlType.Int) }), Array.Empty<Row>());
        var scan = new SeqScan(emptyTable);
        var specs = new[] { new AggregateSpec(AggregateFunc.Count, -1, IsCountStar: true, "COUNT(*)", SqlType.Int) };
        var aggregate = new HashAggregate(scan, groupByIndices: Array.Empty<int>(), specs);

        var rows = aggregate.Execute().ToList();

        Assert.Single(rows);
        Assert.Equal(0L, rows[0][0].AsInt);
    }

    [Fact]
    public void Sort_PutsNullsLast_RegardlessOfDirection()
    {
        var schema = new Schema(new[] { new Column("x", SqlType.Int) });
        var rows = new[]
        {
            new Row(new[] { SqlValue.Int(3) }),
            new Row(new[] { SqlValue.Null(SqlType.Int) }),
            new Row(new[] { SqlValue.Int(1) }),
        };
        var table = new Table("t", schema, rows);

        var ascending = new Sort(new SeqScan(table), new[] { new SortKey(0, Descending: false) }).Execute().ToList();
        Assert.Equal(new[] { 1L, 3L }, ascending.Take(2).Select(r => r[0].AsInt));
        Assert.True(ascending[2][0].IsNull);

        var descending = new Sort(new SeqScan(table), new[] { new SortKey(0, Descending: true) }).Execute().ToList();
        Assert.Equal(new[] { 3L, 1L }, descending.Take(2).Select(r => r[0].AsInt));
        Assert.True(descending[2][0].IsNull);
    }
}
