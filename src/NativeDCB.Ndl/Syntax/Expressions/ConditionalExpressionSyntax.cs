namespace NativeDCB.Ndl;

public sealed record ConditionalExpressionSyntax(
    ExpressionSyntax Condition,
    ExpressionSyntax WhenTrue,
    ExpressionSyntax WhenFalse,
    TextSpan Span) : ExpressionSyntax(Span);