namespace Sift.Core.Sql.Ast;

public sealed record SelectItem(bool IsStar, string? ColumnName, string? Alias);

public sealed record TableRef(string Name, string? Alias)
{
    /// <summary>The name queries should use to refer to this table: its alias if given, else its own name.</summary>
    public string EffectiveName => Alias ?? Name;
}

public sealed record SelectStatement(
    IReadOnlyList<SelectItem> Columns,
    TableRef From,
    Expr? Where,
    int? Limit);
