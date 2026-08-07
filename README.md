# Sift — In-Memory SQL Query Engine

A SQL query engine being built from scratch in C#: a hand-written B+ tree index, a
cost-based query planner, and a pull-based (Volcano) execution model. No database
libraries — SQLite is used only as a test oracle (Phase 4).

See [PLAN.md](PLAN.md) for the full design and phase-by-phase build plan.

## Status: Phase 3 — indexes + cost-based planning

- [x] `Row`, `Column`, `Schema`, `Table`, `Catalog`, `Statistics` (row count + per-column
  distinct-value count, collected on load)
- [x] CSV loader with type inference (`data/fmcg_sales.csv`, 190,757 rows;
  `data/brands.csv`, a 14-row dimension table derived from `fmcg_sales.brand`, for JOIN demos)
- [x] Full SQL grammar from PLAN.md §4, plus `CREATE INDEX ON table(col) [USING (HASH|BTREE)]`
  and a lightweight `EXPLAIN <query>` REPL command
- [x] `SqlValue` / `SqlBool` — explicit-null values with SQL three-valued logic
- [x] Lazy, pull-based (Volcano-style) operator tree: `SeqScan`, `IndexScan`, `Filter`,
  `Project`, `Limit`, `Sort`, `HashAggregate`, `NestedLoopJoin`, `HashJoin`
- [x] Hand-written **B+ tree** index (node splitting, linked leaves for range scans, insert +
  search only — no deletion, per scope) and a **hash index** (exact-match only); both
  capability-aware (`IIndex.SupportsRange`) so the planner never picks a hash index for a range
- [x] **Cost-based scan selection**: `CostModel` compares `SeqScan` vs `IndexScan` cost from
  real table statistics — and correctly *declines its own index* on a low-selectivity predicate
  (`promotion_flag = 0`, ~half the table) in favor of `IndexScan` on a selective one (`sku = '...'`)
- [x] Rule-based rewrite: **predicate pushdown** moves WHERE conjuncts below a JOIN, qualifier-aware
  (so `b.category = 'X'` can't be misattributed to an unrelated same-named column on the other side)
- [x] Logical plan → physical (operator tree) translation, kept as separate types

Not yet built: differential testing against SQLite, benchmarks, and the full cost/row-annotated
`EXPLAIN` — see PLAN.md Phase 4.

## Running it

```bash
dotnet run --project src/Sift.Cli
```

```
sift> CREATE INDEX ON fmcg_sales(sku)
sift> EXPLAIN SELECT * FROM fmcg_sales WHERE sku = 'MI-006'
IndexScan on fmcg_sales using BPlusTree index on sku = MI-006

sift> EXPLAIN SELECT * FROM fmcg_sales WHERE promotion_flag = 0
Filter
  SeqScan on fmcg_sales  (est. rows=190757)
```

## Testing

```bash
dotnet test
```
