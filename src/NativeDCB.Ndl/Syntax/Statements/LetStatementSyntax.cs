namespace NativeDCB.Ndl;

public sealed record LetStatementSyntax(string Name, ExpressionSyntax Value, TextSpan Span)
    : EvaluateStatementSyntax(Span);