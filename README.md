# Sift — In-Memory SQL Query Engine

A SQL query engine being built from scratch in C#: a hand-written B+ tree index, a
cost-based query planner, and a pull-based (Volcano) execution model. No database
libraries — SQLite is used only as a test oracle (Phase 4).

See [PLAN.md](PLAN.md) for the full design and phase-by-phase build plan.

## Status: Phase 2 — the operator model

- [x] `Row`, `Column`, `Schema`, `Table`, `Catalog`
- [x] CSV loader with type inference (`data/fmcg_sales.csv`, 190,757 rows;
  `data/brands.csv`, a 14-row dimension table derived from `fmcg_sales.brand`, for JOIN demos)
- [x] Full SQL grammar from PLAN.md §4: `SELECT ... FROM ... [JOIN ... ON a.col = b.col]
  [WHERE ...] [GROUP BY ...] [HAVING ...] [ORDER BY ...] [LIMIT ...]`, aggregates
  (`COUNT/SUM/AVG/MIN/MAX`), full predicate grammar (`= != < <= > >= BETWEEN IN IS [NOT] NULL AND OR NOT`)
- [x] `SqlValue` / `SqlBool` — explicit-null values with SQL three-valued logic
- [x] Lazy, pull-based (Volcano-style) operator tree: `SeqScan`, `Filter`, `Project`, `Limit`,
  `Sort`, `HashAggregate`, `NestedLoopJoin`, `HashJoin` — verified lazy with a rows-touched
  counter (`Limit` over `Filter` stops early; `Limit` over `Sort`/`HashAggregate` correctly
  can't, since both are blocking operators)
- [x] Logical plan → physical (operator tree) translation, kept as separate types
- [x] CLI REPL that prints a result table

Not yet built: indexes, the cost-based planner, `EXPLAIN`, differential testing, and
benchmarks — see PLAN.md Phases 3–5.

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
