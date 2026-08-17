namespace NativeDCB.Ndl;

public sealed record EmitStatementSyntax(string EventType, ObjectExpressionSyntax Value, TextSpan Span)
    : SyntaxNode(Span);