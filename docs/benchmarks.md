# Benchmarks

## Methodology

- [BenchmarkDotNet](https://benchmarkdotnet.org/) 0.15.8, `[ShortRunJob]` (3 warmup + 3 measured
  iterations, 1 launch) — a reduced iteration count for faster turnaround than the library's
  default (~15 iterations); still real, statistically-summarized measurements from an isolated
  benchmark process, not a single stopwatch run.
- Release build (`-c Release`) only.
- All data from the real `data/fmcg_sales.csv` (190,757 rows, 14 columns) and `data/brands.csv`
  (14 rows), except the selectivity crossover benchmark, which uses a synthetic 190,000-row
  table so selectivity can be swept independently of what values happen to exist in the CSV.
- Reproduce with `dotnet run --project src/InMemorySqlEngine.Bench -c Release -- --filter "*"`.

**Machine**: AMD Ryzen 9 8940HX (16 physical / 32 logical cores), 16 GB RAM, Windows 11,
.NET SDK 8.0.424, .NET runtime 8.0.30.

## Index scan vs sequential scan across selectivity — the headline chart

![SeqScan vs IndexScan crossover chart](crossover.svg)

| Selectivity | SeqScan | IndexScan | Speedup |
|---:|---:|---:|---:|
| 1.0 (matches every row) | 241.2 µs | 252.9 µs | 0.95x (index is *slower*) |
| 0.1 | 340.2 µs | 25.0 µs | 13.6x |
| 0.01 | 277.7 µs | 2.49 µs | 111x |
| 0.001 | 252.8 µs | 0.29 µs | 880x |

This is the planner's own reasoning made visible: `SeqScan` costs about the same regardless of
selectivity — it always touches every row — while `IndexScan` cost scales with the number of
*matched* rows. At selectivity 1.0 (an equality predicate matching the whole table, e.g. a
constant-valued column), the index provides no benefit and even loses slightly to a plain scan's
better cache locality — exactly the case `CostModel` refuses to route to `IndexScan` (the `promotion_flag = 0` scenario in [design.md §5](design.md#5-cost-model-and-the-crossover)). By selectivity 0.001 the index is three orders of magnitude
faster.

## B+ tree vs hash index, exact-match lookup

`sku = 'MI-006'` against the real 190,757-row `fmcg_sales` table (30 distinct sku values, ~6,400
matching rows).

| Method | Mean | Allocated |
|---|---:|---:|
| HashLookup (baseline) | 7.20 µs | 40 B |
| BTreeLookup | 12.24 µs | 40 B |

Hash wins a pure exact-match lookup by ~1.7x — O(1) average-case vs. the B+ tree's O(log n)
traversal. This is exactly why both index types exist: hash for equality-only workloads, B+ tree
for anything that also needs ordering or ranges (next).

## B+ tree range query vs full scan

`price_unit BETWEEN 2.0 AND 3.0` against the same table.

| Method | Mean | Allocated |
|---|---:|---:|
| FullScan (baseline) | 14,460 µs | 32 B |
| RangeScan | 102.5 µs | 312 B |

**141x faster.** A hash index can't answer this query at all (`IIndex.SupportsRange` is `false`
for `HashIndex`) — this is the benchmark that justifies the B+ tree's extra implementation cost
over a plain hash table.

## Nested-loop vs hash join

`fmcg_sales` (190,757 rows) joined to `brands` (14 rows) on `brand`, full result materialized.

| Method | Mean | Allocated |
|---|---:|---:|
| NestedLoop (baseline) | 41.35 ms | 161.6 MB |
| Hash | 30.03 ms | 161.6 MB |

Hash join wins by only ~1.4x. The build side (`brands`) has just 14 rows, so nested-loop's
O(n·m) costs at most 14 comparisons per probe row. Hash join's advantage grows with the size of
the smaller input. Joining two large tables, nested-loop would fall far behind.

## Lazy `LIMIT 10` vs materialized

Pulling 10 rows through the operator tree vs. materializing the full 190,757-row scan first.

| Method | Mean | Allocated |
|---|---:|---:|
| MaterializeThenTake (baseline) | 546,170 ns | 1,526,203 B |
| LazyLimit | 65.7 ns | 176 B |

**~8,300x faster, ~8,700x less allocated.** This measures the laziness described in
[design.md §3](design.md#3-execution-volcano-with-yield-return). `LazyLimit` builds only the 10
rows it returns. The `yield return` chain from `Limit` down to `SeqScan` stops pulling as soon as
the count is reached, so the other 190,747 rows are never materialized.

## Parallel aggregate: 1 / 2 / 4 / 8 threads

`SELECT brand, SUM(units_sold) FROM fmcg_sales GROUP BY brand` (190,757 rows, 14 groups) —
`ParallelHashAggregate` partitions the table into contiguous row ranges, aggregates each
partition independently with no shared mutable state, then merges the per-partition accumulators
single-threaded. Compared against a fixed `HashAggregate` baseline (re-measured at each thread
count to keep the comparison honest — see methodology).

![Parallel GROUP BY speedup vs thread count](parallel-speedup.svg)

| Threads | Sequential (baseline) | Parallel | Speedup | Efficiency |
|---:|---:|---:|---:|---:|
| 1 | 31.40 ms | 30.96 ms | 1.01x | 101% |
| 2 | 30.86 ms | 16.47 ms | 1.87x | 94% |
| 4 | 31.44 ms | 9.13 ms | 3.44x | 86% |
| 8 | 30.89 ms | 5.07 ms | 6.10x | 76% |

**Real, but sub-linear, as expected.** Efficiency falls from 94% at 2 threads to 76% at 8, for
two structural reasons:

1. **The merge is serial.** Only the per-partition scan, filter and accumulate phase runs in
   parallel. Merging the partition results runs on one thread afterward, and that fixed cost is
   a larger share of the total as the parallel part shrinks.
2. **Memory bandwidth is shared.** A scan is bandwidth-bound, not compute-bound. All cores read
   through the same memory controllers, so adding threads doesn't add proportional throughput.

At `Threads=1` the result is ~1.0x, not lower. That shows the partition and merge machinery
adds no measurable overhead on its own.
