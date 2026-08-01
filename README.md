# Sift — In-Memory SQL Query Engine

A SQL query engine being built from scratch in C#: a hand-written B+ tree index, a
cost-based query planner, and a pull-based (Volcano) execution model. No database
libraries — SQLite is used only as a test oracle (Phase 4).

See [PLAN.md](PLAN.md) for the full design and phase-by-phase build plan.

## Status: Phase 1 — storage + minimal end-to-end path

- [x] `Row`, `Column`, `Schema`, `Table`, `Catalog`
- [x] CSV loader with type inference (`data/fmcg_sales.csv`, 190,757 rows)
- [x] Lexer + recursive-descent parser for `SELECT … FROM … WHERE … LIMIT …`
  (full WHERE grammar: `= != < <= > >= BETWEEN IN IS [NOT] NULL`, `AND OR NOT`)
- [x] `SqlValue` / `SqlBool` — explicit-null values with SQL three-valued logic
- [x] Naive executor: scan, filter, project, limit
- [x] CLI REPL that prints a result table

Not yet built: the lazy operator tree, joins, aggregates, indexes, the cost-based
planner, `EXPLAIN`, differential testing, and benchmarks — see PLAN.md Phases 2–5.

## Running it

```bash
dotnet run --project src/Sift.Cli
```

```
sift> SELECT brand, units_sold FROM fmcg_sales WHERE region = 'PL-North' LIMIT 10
```

## Testing

```bash
dotnet test
```
