# Contributing to In-Memory SQL Engine

Thanks for your interest in this project. Bug reports, suggestions and pull requests are welcome.

## Getting started

You need the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0). The version is
pinned in `global.json`.

```bash
dotnet build
dotnet test --filter "Category!=Differential"   # fast unit tests
dotnet test                                     # full suite, including SQLite differential tests (~3 min)
```

## Making changes

- **Keep the scope.** The engine is a read-only, in-memory analytical engine. Before adding SQL
  features, read the scope section of [`docs/design.md`](docs/design.md#1-scope-and-parsing).
  Open an issue first for anything listed there as out of scope.
- **Test every change.**
  - New SQL support needs a parser test and a differential test case that compares the result
    with SQLite.
  - Planner changes should assert the *plan shape* that gets chosen, not only the query results.
  - Performance claims need a BenchmarkDotNet benchmark run in Release mode.
- **Build cleanly.** Warnings are treated as errors (`Directory.Build.props`), and code style
  follows `.editorconfig`.
- **Update the docs.** If behavior changes, update the README, the relevant file in `docs/`,
  and `CHANGELOG.md`.

## Pull requests

1. Fork the repository and create a branch from `master`.
2. Make your change, with tests.
3. Make sure `dotnet test` passes, including the differential suite.
4. Open a pull request that describes what changed and why.

## Reporting bugs

Open an issue that includes:

- the SQL query,
- the result you expected (ideally SQLite's output for the same query and data),
- the result the engine returned, or the error message,
- the data involved, if it isn't the bundled dataset.

## License

By contributing, you agree that your contributions are licensed under the [MIT License](LICENSE).
