namespace NativeDCB.Ndl;

public sealed record RequireStatementSyntax(ExpressionSyntax Condition, ExpressionSyntax Reason, TextSpan Span)
    : EvaluateStatementSyntax(Span);