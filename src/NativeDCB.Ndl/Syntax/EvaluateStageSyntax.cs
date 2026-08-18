namespace NativeDCB.Ndl;

public sealed record EvaluateStageSyntax(IReadOnlyList<EvaluateStatementSyntax> Statements, TextSpan Span)
    : SyntaxNode(Span);