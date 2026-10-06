using InMemorySqlEngine.Core.Catalog;
using InMemorySqlEngine.Core.Values;

namespace InMemorySqlEngine.Core.Execution;

public interface IPredicate
{
    SqlBool Evaluate(Row row);
}

/// <summary>Wraps an AST expression as an <see cref="IPredicate"/> against a fixed schema.</summary>
public sealed class ExprPredicate : IPredicate
{
    private readonly Sql.Ast.Expr _expr;
    private readonly Schema _schema;

    public ExprPredicate(Sql.Ast.Expr expr, Schema schema)
    {
        _expr = expr;
        _schema = schema;
    }

    public SqlBool Evaluate(Row row) => Evaluator.EvalPredicate(_expr, row, _schema);
}
