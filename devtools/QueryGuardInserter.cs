using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Bimtech.RevitMcp.DevTools;

// Rewrites a send_code snippet so it can stop itself (see QueryGuard):
//   - every loop body starts with QueryGuard.Tick();
//   - every local function, lambda and anonymous method becomes { Enter(); try { <body> } finally { Exit(); } }.
// Expression bodies become blocks; the semantic model tells whether the body returns a value. Lambdas converted to
// expression trees are left alone, since a block cannot be one. Errors are reported from the snippet as written,
// before this runs, so line numbers match.
internal sealed class QueryGuardInserter(SemanticModel model) : CSharpSyntaxRewriter
{
    private const string Guard = "global::Bimtech.RevitMcp.DevTools.QueryGuard";

    public static SyntaxTree Insert(SyntaxTree tree, SemanticModel model) =>
        tree.WithRootAndOptions(new QueryGuardInserter(model).Visit(tree.GetRoot()), tree.Options);

    // ---- Loops

    public override SyntaxNode? VisitForStatement(ForStatementSyntax node)
    {
        var visited = (ForStatementSyntax)base.VisitForStatement(node)!;
        return visited.WithStatement(Ticked(visited.Statement));
    }

    public override SyntaxNode? VisitForEachStatement(ForEachStatementSyntax node)
    {
        var visited = (ForEachStatementSyntax)base.VisitForEachStatement(node)!;
        return visited.WithStatement(Ticked(visited.Statement));
    }

    public override SyntaxNode? VisitForEachVariableStatement(ForEachVariableStatementSyntax node)
    {
        var visited = (ForEachVariableStatementSyntax)base.VisitForEachVariableStatement(node)!;
        return visited.WithStatement(Ticked(visited.Statement));
    }

    public override SyntaxNode? VisitWhileStatement(WhileStatementSyntax node)
    {
        var visited = (WhileStatementSyntax)base.VisitWhileStatement(node)!;
        return visited.WithStatement(Ticked(visited.Statement));
    }

    public override SyntaxNode? VisitDoStatement(DoStatementSyntax node)
    {
        var visited = (DoStatementSyntax)base.VisitDoStatement(node)!;
        return visited.WithStatement(Ticked(visited.Statement));
    }

    // ---- Functions (the semantic model is asked about the original node, before rewriting)

    public override SyntaxNode? VisitLocalFunctionStatement(LocalFunctionStatementSyntax node)
    {
        var method = model.GetDeclaredSymbol(node) as IMethodSymbol;
        var visited = (LocalFunctionStatementSyntax)base.VisitLocalFunctionStatement(node)!;
        if (visited.Body is { } body) return visited.WithBody(Counted(body.Statements));
        if (visited.ExpressionBody is not { } arrow) return visited;
        return visited.WithExpressionBody(null).WithSemicolonToken(default)
            .WithBody(Counted([BodyStatement(arrow.Expression, method)]));
    }

    public override SyntaxNode? VisitParenthesizedLambdaExpression(ParenthesizedLambdaExpressionSyntax node)
    {
        var method = Lambda(node);
        var visited = (ParenthesizedLambdaExpressionSyntax)base.VisitParenthesizedLambdaExpression(node)!;
        if (method is null) return visited;
        return visited.Block is { } block
            ? visited.WithBlock(Counted(block.Statements))
            : visited.WithExpressionBody(null).WithBlock(Counted([BodyStatement(visited.ExpressionBody!, method)]));
    }

    public override SyntaxNode? VisitSimpleLambdaExpression(SimpleLambdaExpressionSyntax node)
    {
        var method = Lambda(node);
        var visited = (SimpleLambdaExpressionSyntax)base.VisitSimpleLambdaExpression(node)!;
        if (method is null) return visited;
        return visited.Block is { } block
            ? visited.WithBlock(Counted(block.Statements))
            : visited.WithExpressionBody(null).WithBlock(Counted([BodyStatement(visited.ExpressionBody!, method)]));
    }

    public override SyntaxNode? VisitAnonymousMethodExpression(AnonymousMethodExpressionSyntax node)
    {
        var visited = (AnonymousMethodExpressionSyntax)base.VisitAnonymousMethodExpression(node)!;
        return visited.WithBlock(Counted(visited.Block.Statements));
    }

    // The lambda's method, or null when it must stay as written (an expression tree, or unknown).
    private IMethodSymbol? Lambda(LambdaExpressionSyntax node)
    {
        if (model.GetTypeInfo(node).ConvertedType is INamedTypeSymbol { Name: "Expression" } type
            && type.ContainingNamespace?.ToDisplayString() == "System.Linq.Expressions")
            return null;
        return model.GetSymbolInfo(node).Symbol as IMethodSymbol;
    }

    // "expr;" when the function returns nothing (void, or async Task/ValueTask), else "return expr;".
    private static StatementSyntax BodyStatement(ExpressionSyntax expression, IMethodSymbol? method)
    {
        var returnsNothing = method is null
            || method.ReturnsVoid
            || (method.IsAsync && method.ReturnType is INamedTypeSymbol { IsGenericType: false, Name: "Task" or "ValueTask" });
        return returnsNothing
            ? SyntaxFactory.ExpressionStatement(expression)
            : SyntaxFactory.ReturnStatement(expression);
    }

    private static StatementSyntax Call(string name) => SyntaxFactory.ParseStatement($"{Guard}.{name}();");

    private static BlockSyntax Ticked(StatementSyntax body) =>
        body is BlockSyntax block ? block.WithStatements(block.Statements.Insert(0, Call("Tick"))) : SyntaxFactory.Block(Call("Tick"), body);

    private static BlockSyntax Counted(IEnumerable<StatementSyntax> statements) =>
        SyntaxFactory.Block(
            Call("Enter"),
            SyntaxFactory.TryStatement(
                SyntaxFactory.Block(statements),
                default,
                SyntaxFactory.FinallyClause(SyntaxFactory.Block(Call("Exit")))));
}
