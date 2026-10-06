# Design notes

These notes explain the main design decisions in the engine and the reasons for them.
For a walkthrough of each component, see [`architecture.md`](architecture.md). For measured
numbers, see [`benchmarks.md`](benchmarks.md).

## 1. Scope and parsing

I kept the SQL front end small on purpose. Parsing is the least interesting part of a database,
so most of the work went into planning, indexing, execution and testing.

### Supported grammar

```sql
SELECT  col, col, AGG(col) [AS alias]
FROM    table [alias]
[JOIN   table [alias] ON a.col = b.col] ...
[WHERE  predicate [AND|OR predicate]]
[GROUP BY col, col]
[HAVING predicate]
[ORDER BY col [ASC|DESC]]
[LIMIT  n]
```

- Predicates: `= != < <= > >= BETWEEN IN IS NULL IS NOT NULL`
- Aggregates: `COUNT, SUM, AVG, MIN, MAX`
- Types: `INT, DECIMAL, TEXT, DATE, BOOL`
- Commands: `EXPLAIN <query>`, `CREATE INDEX ON table(col) [USING HASH|BTREE]`

There are no arithmetic expressions, so a `-` directly before a digit is always the sign of a
literal. The lexer depends on this.

### Out of scope

- Subqueries, `UNION`, window functions, `CASE`, `DISTINCT`
- `INSERT` / `UPDATE` / `DELETE`. The engine is a read-only, in-memory analytical engine.
- Disk persistence, WAL, transactions, MVCC, locking
- B+ tree deletion. A read-only engine only needs insert and search, and rebalancing on
  delete is complex.
- Multi-column indexes, outer joins, and join reordering. Joins run in the order written,
  so put the smaller table on the right of each `JOIN`.

## 2. Pipeline

```
SQL text → Parser → AST → Logical plan → Rewrites (predicate pushdown)
         → Physical plan (cost-based scan choice) → Volcano operator tree → rows
```

The logical plan describes *what* to compute. The physical plan describes *how*. Keeping them
as separate types means rewrite rules work on the logical tree, and the cost-based choice
between `SeqScan` and `IndexScan` only happens when building the physical tree.

## 3. Execution: Volcano with `yield return`

Every operator derives from `Operator` and exposes `IEnumerable<Row> Execute()`. A parent
pulls rows from its child one at a time. In C#, `yield return` implements this pull model
directly:

```csharp
public override IEnumerable<Row> Execute()
{
    foreach (var row in _child.Execute())
        if (_predicate.Evaluate(row) == SqlBool.True)
            yield return row;
}
```

This has two effects:

- **Memory stays flat.** Streaming operators never materialize their input. Only `Sort` and
  `HashAggregate` must buffer.
- **`LIMIT` short-circuits.** `LIMIT 10` stops pulling after 10 rows, so the scan below it never
  reads the rest of the table. I verified this with a rows-touched counter, not just timings.
  It runs about 8,300x faster than materializing the scan first.

`HashJoin` builds a hash table over the right input and streams the left input, so it runs in
O(n+m). `NestedLoopJoin` exists as the O(n·m) baseline it is benchmarked against.

## 4. Indexes

- **`HashIndex`**: exact match only, with O(1) average lookup and no ordering.
- **`BPlusTreeIndex`**: hand-written, with real node splitting and linked leaves for range
  scans. It supports insert and search only. I didn't use `SortedDictionary` because building
  the structure was the point, and the BCL type doesn't expose linked leaves or fan-out.

Each index declares what it can do (`IIndex.SupportsRange`). The planner checks this, so it
never routes a range predicate to a hash index and never builds a physical plan that can't
execute.

## 5. Cost model and the crossover

Most small engines use the rule "an index exists, so use it". That rule is wrong. If a
predicate matches most of the table, an index scan pays a tree traversal plus a random access
for every matched row. A sequential scan makes one cache-friendly sweep instead.

`Statistics` collects the row count and the distinct-value count for each column at load time.
`CostModel` then estimates:

- selectivity ≈ `1 / distinctValues` for `=`, and a fixed default fraction for ranges
- `seqScanCost = rowCount × scanCostPerRow`
- `indexScanCost = treeTraversal(rowCount) + matchedRows × randomAccessCostPerRow`

The planner splits the `WHERE` clause into conjuncts and chooses the cheaper plan. With an
index on both columns, `sku = 'MI-006'` gets an `IndexScan`, and `promotion_flag = 0` (about half
the table) gets a `SeqScan`. The planner turns down its own index.

The benchmark confirms the model. On a synthetic 190,000-row table, `IndexScan` is 880x faster
at selectivity 0.001 and about 5% *slower* (0.95x) at selectivity 1.0.

Real systems use histograms for skewed data. The engine uses uniform distinct-count estimates on
purpose, and histograms would be the next step.

### Predicate pushdown

Predicate pushdown is a rule-based rewrite. It moves single-table filters below a `JOIN`, so the
join only sees rows that already passed their own table's filters. The rewrite is
qualifier-aware. A same-named column on the wrong side of a join is never misattributed.

## 6. NULL semantics

SQL uses three-valued logic: `TRUE`, `FALSE` and `UNKNOWN`. The engine implements it with a `SqlValue`
struct that carries an explicit null flag, plus a `SqlBool { True, False, Unknown }` enum. It
does not use C#'s `bool?`.

- `NULL = NULL` is `UNKNOWN`, not `TRUE`.
- `WHERE` keeps a row only when the predicate is `TRUE`.
- `COUNT(col)` skips NULLs, and `COUNT(*)` counts them.
- `SUM` and `AVG` ignore NULLs. `AVG` over only NULLs returns `NULL`, not `0`.
- A `GROUP BY` with no input groups returns no rows. A bare aggregate with no input returns
  one row.
- `ORDER BY` always sorts NULLs last, for both `ASC` and `DESC`. This is a fixed, documented
  rule. SQLite sorts NULLs first in ascending order, so the differential tests compare result
  sets rather than row order.

## 7. Testing: SQLite as the oracle

Unit tests check each piece separately: B+ tree invariants after random inserts, parser tests
including malformed input, operators on small fixed tables, and the *shape* of the plan the planner picks.

For end-to-end correctness, SQLite (`Microsoft.Data.Sqlite`) serves as a **test oracle**. The
same CSV files load into both engines, the same SQL runs against both, and the result sets
must match. A random query generator produces 500 valid queries in the engine's grammar on every
test run, alongside targeted cases.

The oracle found real bugs. One was `!=` matching NULL rows. Another was an ambiguous
unqualified column after a join, which is documented under *Known limitations* in the
README.

## 8. Parallel aggregation

`ParallelHashAggregate` splits the table into partitions. Each partition runs a fused
scan, filter and partial aggregate with no shared mutable state. A single thread then merges
the partial results. It reaches 6.1x at 8 threads on a 16-core machine. The speedup is
sub-linear because of memory bandwidth and merge overhead, as expected. The planner doesn't
choose it automatically yet. That would need a cost model for when parallelism pays off.

## 9. Benchmark methodology

- All benchmarks use BenchmarkDotNet in Release builds only, with warm-up, multiple
  iterations and allocation reporting.
- `benchmarks.md` states the job configuration and machine spec.
- The selectivity sweep uses synthetic data so selectivity can vary independently of the
  CSV's actual values.

## 10. Pitfalls I ran into or avoided

- **Culture-sensitive string comparison.** I use `StringComparer.Ordinal` everywhere.
  Otherwise results quietly differ from SQLite.
- **`decimal` vs `double`.** Money-like columns use `decimal`. `double` drift breaks exact
  comparison with the oracle.
- **Benchmarking Debug builds.** I always build with `-c Release`.
- **Boxing in hot loops.** A value struct that boxes on every comparison dominates the
  profile.
- **Claiming laziness without measuring it.** I count the rows touched instead of relying on
  timings.
- **Scope creep.** I treated the out-of-scope list in §1 as binding.
