namespace Sift.Core.Sql.Ast;

public abstract record Statement;

/// <summary>A single SELECT list entry: `*`, a column, or an aggregate call, with an optional alias.</summary>
public sealed record SelectItem(bool IsStar, Expr? Expression, string? Alias)
{
    /// <summary>The name this item is known by downstream (HAVING/ORDER BY resolve against this).</summary>
    public string ResolvedName => Alias ?? Expression switch
    {
        ColumnRefExpr col => col.ColumnName,
        AggregateExpr agg => agg.CanonicalName,
        _ => throw new InvalidOperationException($"select item '{Expression}' has no resolvable name")
    };
}

public sealed record TableRef(string Name, string? Alias)
{
    /// <summary>The name queries should use to refer to this table: its alias if given, else its own name.</summary>
    public string EffectiveName => Alias ?? Name;
}

/// <summary>`JOIN table ON left = right` — the grammar only allows a single-column equi-join.</summary>
public sealed record JoinClause(TableRef Table, ColumnRefExpr LeftColumn, ColumnRefExpr RightColumn);

public sealed record OrderByItem(ColumnRefExpr Column, bool Descending);

public sealed record SelectStatement(
    IReadOnlyList<SelectItem> Columns,
    TableRef From,
    IReadOnlyList<JoinClause> Joins,
    Expr? Where,
    IReadOnlyList<ColumnRefExpr> GroupBy,
    Expr? Having,
    IReadOnlyList<OrderByItem> OrderBy,
    int? Limit) : Statement;

public enum IndexTypeHint { BTree, Hash }

/// <summary>`CREATE INDEX ON table(col) [USING (HASH | BTREE)]` — no index name; one index per (table, column).</summary>
public sealed record CreateIndexStatement(string TableName, string ColumnName, IndexTypeHint Kind) : Statement;
