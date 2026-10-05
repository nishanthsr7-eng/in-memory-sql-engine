# Sift — In-Memory SQL Query Engine

A SQL query engine built from scratch in C#: a hand-written B+ tree index, a cost-based query
planner, and a pull-based (Volcano) execution model. No database libraries — SQLite is used
only as a test oracle.

Design notes (scope, execution model, cost model, NULL semantics, testing): [`docs/design.md`](docs/design.md).

## Highlights

- **Cost-based planner** compares real `SeqScan` vs `IndexScan` cost from table statistics — and
  correctly *declines its own index* on a low-selectivity predicate (`promotion_flag = 0`, ~half
  the table) in favor of using it on a selective one (`sku = '...'`). Verified both by unit tests
  and by a benchmark: [880x faster at selectivity 0.001, ~5% *slower* at selectivity 1.0](docs/benchmarks.md).
- **Hand-written B+ tree** (real node splitting, linked leaves, range scans) and a hash index,
  both capability-aware — the planner never routes a range predicate to a hash index.
- **Lazy, pull-based (Volcano) operator tree** — `LIMIT 10` on 190k rows is ~8,300x faster and
  ~8,700x less allocation than materializing the scan first ([benchmarked](docs/benchmarks.md)).
- **Differential tested against SQLite**: 500 randomly generated queries plus targeted
  correctness tests, run every `dotnet test`, both against the real 190,757-row dataset.
- **Correct SQL three-valued NULL logic**, cost/row-annotated `EXPLAIN`, and a rule-based
  predicate-pushdown rewrite that's qualifier-aware (so a same-named column on the wrong side of
  a JOIN can't get silently misattributed — see *Known limitations*).
- **Parallel aggregation** (`ParallelHashAggregate`): partitions the table, aggregates each
  partition independently with zero shared mutable state, merges single-threaded. 6.1x speedup
  at 8 threads on a 16-core machine — sub-linear, and [why that's the expected result, not a
  shortfall, is in the benchmark writeup](docs/benchmarks.md#parallel-aggregate-1--2--4--8-threads).

## Status: complete (Phases 1–5)

- [x] Phase 1 — storage, CSV loading, a naive scan/filter/project executor, CLI REPL
- [x] Phase 2 — the lazy, pull-based (Volcano) operator tree: scan, filter, project, limit, sort,
  hash aggregate, nested-loop/hash join; logical → physical plan translation
- [x] Phase 3 — hand-written B+ tree + hash index, table statistics, cost-based scan selection,
  qualifier-aware predicate pushdown
- [x] Phase 4 — cost/row-annotated `EXPLAIN`, SQLite differential testing (500-query fuzzer +
  targeted cases), BenchmarkDotNet suite with real measured numbers
- [x] Phase 5 — parallel `GROUP BY`/aggregate execution, benchmarked at 1/2/4/8 threads

Design rationale for every phase: [`docs/design.md`](docs/design.md).

## Architecture

![Sift query pipeline](docs/architecture.svg)

Stage-by-stage walkthrough of every component pictured above: [`docs/architecture.md`](docs/architecture.md).

## Known limitations

- **Ambiguous unqualified columns after a JOIN are not rejected.** If two joined tables share a
  column name (e.g. both have `category`), Sift resolves an *unqualified* reference to whichever
  table's column was registered last in the combined schema, rather than raising an error the
  way SQLite does. Always qualify (`b.category`) when a name could exist on either side — this
  was caught by the SQLite differential oracle itself (see `DifferentialTests.Join_MatchesSqlite`
  and the git history for `PredicatePushdown`, which *is* qualifier-aware for exactly this reason).
- **`ORDER BY` can't reference a column outside the SELECT list *and* a SELECT alias
  simultaneously** — deliberate scoping, see the comment in `Planner.BuildLogicalPlan`.
- **`ParallelHashAggregate` isn't reachable from SQL.** It's a real, tested, benchmarked operator
  (`src/Sift.Core/Execution/ParallelHashAggregate.cs`), but the planner never chooses it
  automatically — that would need a cost model for *when parallelism is worth it* (small
  aggregates lose to partition/merge overhead; see the benchmark writeup), which is out of scope
  for what Phase 5 asks for. Used directly today, e.g. from `Sift.Bench`.

## EXPLAIN example

_(column list on `Project` abbreviated below for readability — the CLI prints all 14)_

```
sift> CREATE INDEX ON fmcg_sales(sku)
sift> EXPLAIN SELECT * FROM fmcg_sales WHERE sku = 'MI-006'
Project (...)  (cost=25787.3, est. rows=6359)
  IndexScan on fmcg_sales using BPlusTree index on sku = MI-006  (cost=25469.3, est. rows=6359)

sift> EXPLAIN SELECT * FROM fmcg_sales WHERE promotion_flag = 0
Project (...)  (cost=233677.3, est. rows=95378)
  Filter  (cost=228908.4, est. rows=95378)
    SeqScan on fmcg_sales  (cost=190757.0, est. rows=190757)
```

## Benchmarks

![SeqScan vs IndexScan crossover chart](docs/crossover.svg)
![Parallel GROUP BY speedup vs thread count](docs/parallel-speedup.svg)

Full methodology, machine spec, and every benchmark (index lookup, range scan, join, laziness,
parallel aggregate) in [`docs/benchmarks.md`](docs/benchmarks.md).

## Running it

```bash
dotnet run --project src/Sift.Cli
```

```
sift> SELECT category, COUNT(*) AS n, SUM(s.units_sold) AS total FROM fmcg_sales s JOIN brands b ON s.brand = b.brand WHERE promotion_flag = 1 GROUP BY category HAVING COUNT(*) > 100 ORDER BY total DESC
```

## Testing

```bash
dotnet test
```

Runs the unit test suite plus the differential suite (500 random queries + targeted cases
against SQLite) — expect the differential run to take ~2–3 minutes; it loads the real
190,757-row dataset into both engines.

## Benchmarking

```bash
dotnet run --project src/Sift.Bench -c Release -- --filter "*"
```

## License

[MIT](LICENSE)
