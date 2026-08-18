namespace NativeDCB.Ndl;

public sealed record IncludeStageSyntax(
    string EventType,
    string Alias,
    ExpressionSyntax Where,
    ObjectExpressionSyntax Apply,
    TextSpan Span) : SyntaxNode(Span);