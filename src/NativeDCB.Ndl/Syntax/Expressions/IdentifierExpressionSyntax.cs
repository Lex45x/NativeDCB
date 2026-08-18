namespace NativeDCB.Ndl;

public sealed record IdentifierExpressionSyntax(string Name, TextSpan Span) : ExpressionSyntax(Span);