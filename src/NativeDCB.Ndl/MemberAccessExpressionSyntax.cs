namespace NativeDCB.Ndl;

public sealed record MemberAccessExpressionSyntax(ExpressionSyntax Target, string Member, TextSpan Span)
    : ExpressionSyntax(Span);