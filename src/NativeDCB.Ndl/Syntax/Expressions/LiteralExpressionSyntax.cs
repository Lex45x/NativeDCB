namespace NativeDCB.Ndl;

public sealed record LiteralExpressionSyntax(LiteralKind Kind, object? Value, string Text, TextSpan Span)
    : ExpressionSyntax(Span);