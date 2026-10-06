# Changelog

All notable changes to this project are documented here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and the project uses
[Semantic Versioning](https://semver.org/).

## [1.0.0] - 2026-10-06

First release.

### Added

- **SQL front end:** a hand-written lexer and recursive-descent parser for a read-only subset of
  SQL: `SELECT`, `JOIN`, `WHERE`, `GROUP BY`, `HAVING`, `ORDER BY`, `LIMIT`, `EXPLAIN` and
  `CREATE INDEX`.
- **Catalog:** CSV loading with type inference (`INT`, `DECIMAL`, `TEXT`, `DATE`, `BOOL`) and
  per-column statistics.
- **Execution:** a lazy, pull-based Volcano operator tree with `SeqScan`, `IndexScan`, `Filter`,
  `Project`, `Sort`, `Limit`, `HashAggregate`, `HashJoin` and `NestedLoopJoin`.
- **Indexes:** a hand-written B+ tree with node splitting, linked leaves and range scans, plus a
  hash index. The planner knows which predicates each index supports.
- **Planning:** a cost-based choice between `SeqScan` and `IndexScan`, and qualifier-aware
  predicate pushdown.
- **Correctness:** three-valued NULL logic, with NULLs always sorting last.
- **Parallelism:** `ParallelHashAggregate`, a partitioned parallel `GROUP BY`.
- **CLI:** an interactive REPL with `.tables` and `.schema`. It loads data from `data/` by
  default, or from `--data <dir>` or CSV paths.
- **Tests:** unit tests, and a differential test suite against SQLite with a 500-query random
  generator.
- **Benchmarks:** a BenchmarkDotNet suite covering the scan crossover, index lookups, range
  scans, joins, laziness and parallel speedup.
- **Project setup:** GitHub Actions CI, `global.json`, `.editorconfig` and build-wide
  warnings-as-errors.

[1.0.0]: https://github.com/nishanthsr7-eng/in-memory-sql-engine/releases/tag/v1.0.0
