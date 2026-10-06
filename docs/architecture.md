# Architecture

![Query pipeline](architecture.svg)

## Pipeline, stage by stage

**Lexer + Parser** (`src/InMemorySqlEngine.Core/Sql/`) — hand-rolled recursive descent, no generator, no
regex. Turns SQL text into an AST (`Sql/Ast/`). Deliberately the least interesting layer by
design ([design.md §1](design.md#1-scope-and-parsing)): the grammar is a fixed, documented subset of ANSI SQL, not an attempt
at full compatibility, so the parser stays small while the planner and execution engine — the
actual point of this project — get the bulk of the effort.

**Logical Plan** (`Planning/LogicalPlan.cs`) — a tree of `Scan` / `Join` / `Filter` / `Aggregate`
/ `Sort` / `Project` / `Limit` nodes describing *what* to compute, independent of how. Kept as a
genuinely separate type from the physical plan so a rewrite rule has something to rewrite before
any physical strategy is chosen.

**PredicatePushdown** (`Planning/Rules/PredicatePushdown.cs`) — the one rule-based rewrite: splits
a WHERE clause into its AND-conjuncts and moves each one below a JOIN if it only references one
side. Qualifier-aware — `b.category` can't get misattributed to an unrelated same-named column
on the other side of the join (a bug the SQLite differential tests caught; see the
README's *Known limitations*).

**Planner + CostModel** (`Planning/Planner.cs`, `Planning/CostModel.cs`) — the one place a real
choice gets made: when a Filter sits directly over a Scan, the planner compares real `SeqScan`
vs `IndexScan` cost (built from `Catalog.Statistics` — row count and per-column distinct-value
count, collected once at load) and picks whichever is cheaper. This is what lets the planner
*decline* its own index on a low-selectivity predicate — see the README highlights and
[`docs/benchmarks.md`](benchmarks.md) for the measured crossover. Everywhere else, one logical
node maps to exactly one physical operator — there's no second axis of choice yet (e.g. join
algorithm or join order), so a separate physical-plan IR would just mirror `Operator` with
nothing to represent.

**Indexing** (`Indexing/`) — `BPlusTreeIndex` (hand-written node splitting, linked leaves for
range scans, insert + search only) and `HashIndex` (O(1) exact match, `Dictionary`-backed). Both
implement `IIndex`, and `IIndex.SupportsRange` makes the B+ tree's extra capability visible to
the planner *before* it commits to a physical operator — a hash index is never routed a range
predicate it can't answer.

**Physical Plan = Operator tree** (`Execution/`) — the compiled plan *is* what runs; there's no
separate execution IR to translate into. Every operator is a pull-based (Volcano-style) iterator:
`Execute()` returns `IEnumerable<Row>` built with `yield return` throughout, so pulling one row
from the root only does as much work as that one row requires. `Limit` over `Filter` proves this
with a rows-touched counter in tests; `Limit` over `Sort`/`HashAggregate` correctly *can't* stay
lazy, since both are blocking operators that must see their whole input before producing a first
row — that distinction is documented directly in `Sort.cs` and `HashAggregate.cs`.

`ParallelHashAggregate` is the one operator that isn't reachable from the composable tree by
design: partitioning only makes sense starting from a raw, random-access `Table`, not an
arbitrary upstream `IEnumerable<Row>`. It splits the table into row-range partitions, aggregates
each independently with zero shared mutable state, then merges single-threaded — see
[`docs/benchmarks.md`](benchmarks.md) for the measured (sub-linear, and why) speedup curve.

## Values and NULL semantics

`SqlValue` (`Values/SqlValue.cs`) is an explicit-null struct — not `object` or `bool?` — so NULL-
ness is a checked property, not something a caller can silently forget. Comparisons return
`SqlBool { True, False, Unknown }` per SQL's three-valued logic (`NULL = NULL` is `Unknown`, not
`True`; `WHERE` keeps only `True` rows). `Equals`/`GetHashCode` treat `Int` and `Decimal` values
as numerically interchangeable (`5` equals `5.0`) — both funnel through the same canonical
`decimal` conversion for hashing, specifically so that invariant holds for `GetHashCode` too, not
just `Equals` (a bug found and fixed during review: `long.GetHashCode()` and
`decimal.GetHashCode()` disagree for negative values even when numerically equal).

## Proof, not just a build

- **`EXPLAIN`** prints the physical plan with real cost/row estimates on every operator — the
  same numbers the planner used to decide, not display-only figures.
- **Differential testing against SQLite** (`tests/InMemorySqlEngine.Tests.Differential/`): the same CSVs
  loaded into both engines, the same SQL text run against both, results compared as canonicalized
  sets. A 500-query random generator plus targeted correctness tests run on every `dotnet test`.
- **BenchmarkDotNet** (`src/InMemorySqlEngine.Bench/`, [`docs/benchmarks.md`](benchmarks.md)): every claim in
  the README is a real measured number from this suite, not an assumed or estimated one.
