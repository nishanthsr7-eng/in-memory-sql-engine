using Sift.Core.Catalog;
using Sift.Core.Execution;
using Sift.Core.Sql.Ast;
using Sift.Core.Values;
using Xunit;

namespace Sift.Tests.Execution;

public class ParallelHashAggregateTests
{
    private static Table BuildTable(int rowCount, int groupCount)
    {
        var schema = new Schema(new[]
        {
            new Column("group_key", SqlType.Int),
            new Column("amount", SqlType.Decimal),
        });
        var rng = new Random(7);
        var rows = new Row[rowCount];
        for (var i = 0; i < rowCount; i++)
        {
            // ~10% NULLs, so the parallel/sequential agreement test also covers NULL-skipping across a merge.
            var amount = rng.Next(10) == 0 ? SqlValue.Null(SqlType.Decimal) : SqlValue.Decimal(rng.Next(1, 100));
            rows[i] = new Row(new[] { SqlValue.Int(rng.Next(groupCount)), amount });
        }
        return new Table("t", schema, rows);
    }

    private static Dictionary<string, SqlValue[]> Run(Operator op) =>
        op.Execute().ToDictionary(r => r[0].ToString(), r => r.Values[1..]);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(17)] // deliberately not a divisor of the row count, and more partitions than some real degrees of parallelism would use
    public void AgreesWithSequentialHashAggregate_AcrossDegreesOfParallelism(int degreeOfParallelism)
    {
        var table = BuildTable(rowCount: 5000, groupCount: 13);
        var specs = new[]
        {
            new AggregateSpec(AggregateFunc.Count, ArgumentColumnIndex: -1, IsCountStar: true, "COUNT(*)", SqlType.Int),
            new AggregateSpec(AggregateFunc.Sum, ArgumentColumnIndex: 1, IsCountStar: false, "SUM(amount)", SqlType.Decimal),
            new AggregateSpec(AggregateFunc.Avg, ArgumentColumnIndex: 1, IsCountStar: false, "AVG(amount)", SqlType.Decimal),
            new AggregateSpec(AggregateFunc.Min, ArgumentColumnIndex: 1, IsCountStar: false, "MIN(amount)", SqlType.Decimal),
            new AggregateSpec(AggregateFunc.Max, ArgumentColumnIndex: 1, IsCountStar: false, "MAX(amount)", SqlType.Decimal),
        };

        var sequential = Run(new HashAggregate(new SeqScan(table), new[] { 0 }, specs));
        var parallel = Run(new ParallelHashAggregate(table, filter: null, new[] { 0 }, specs, degreeOfParallelism));

        Assert.Equal(sequential.Keys.OrderBy(k => k), parallel.Keys.OrderBy(k => k));
        foreach (var key in sequential.Keys)
            Assert.Equal(sequential[key].Select(v => v.ToString()), parallel[key].Select(v => v.ToString()));
    }

    [Fact]
    public void AgreesWithSequential_WithAFilterApplied()
    {
        var table = BuildTable(rowCount: 2000, groupCount: 5);
        var specs = new[] { new AggregateSpec(AggregateFunc.Sum, 1, false, "SUM(amount)", SqlType.Decimal) };
        var predicate = new ExprPredicate(
            new ComparisonExpr(new ColumnRefExpr(null, "group_key"), ComparisonOp.NotEq, new LiteralExpr(SqlValue.Int(2))),
            table.Schema);

        var sequential = Run(new HashAggregate(new Filter(new SeqScan(table), predicate), new[] { 0 }, specs));
        var parallel = Run(new ParallelHashAggregate(table, predicate, new[] { 0 }, specs, degreeOfParallelism: 4));

        Assert.Equal(sequential.Keys.OrderBy(k => k), parallel.Keys.OrderBy(k => k));
        Assert.DoesNotContain("2", parallel.Keys);
        foreach (var key in sequential.Keys)
            Assert.Equal(sequential[key][0].ToString(), parallel[key][0].ToString());
    }

    [Fact]
    public void NoGroupBy_OverEmptyTable_StillProducesOneRow()
    {
        var emptyTable = new Table("t", new Schema(new[] { new Column("x", SqlType.Int) }), Array.Empty<Row>());
        var specs = new[] { new AggregateSpec(AggregateFunc.Count, -1, true, "COUNT(*)", SqlType.Int) };

        var rows = new ParallelHashAggregate(emptyTable, null, Array.Empty<int>(), specs, degreeOfParallelism: 4).Execute().ToList();

        Assert.Single(rows);
        Assert.Equal(0L, rows[0][0].AsInt);
    }

    [Fact]
    public void MorePartitionsThanRows_StillCorrect()
    {
        var table = BuildTable(rowCount: 3, groupCount: 2);
        var specs = new[] { new AggregateSpec(AggregateFunc.Count, -1, true, "COUNT(*)", SqlType.Int) };

        var sequential = Run(new HashAggregate(new SeqScan(table), new[] { 0 }, specs));
        var parallel = Run(new ParallelHashAggregate(table, null, new[] { 0 }, specs, degreeOfParallelism: 16));

        Assert.Equal(sequential.Keys.OrderBy(k => k), parallel.Keys.OrderBy(k => k));
        foreach (var key in sequential.Keys)
            Assert.Equal(sequential[key][0].ToString(), parallel[key][0].ToString());
    }
}
