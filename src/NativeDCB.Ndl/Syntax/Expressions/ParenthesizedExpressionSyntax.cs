namespace NativeDCB.Ndl;

public sealed record ParenthesizedExpressionSyntax(ExpressionSyntax Expression, TextSpan Span) : ExpressionSyntax(Span);