using Sift.Core.Values;

namespace Sift.Core.Catalog;

public sealed record Column(string Name, SqlType Type);
