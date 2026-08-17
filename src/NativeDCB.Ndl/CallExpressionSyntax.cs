namespace NativeDCB.Ndl;

public sealed record CallExpressionSyntax(
    ExpressionSyntax Target,
    IReadOnlyList<ExpressionSyntax> Arguments,
    TextSpan Span) : ExpressionSyntax(Span);