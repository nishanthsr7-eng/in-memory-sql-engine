using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Catalog;

public sealed record Column(string Name, SqlType Type);
