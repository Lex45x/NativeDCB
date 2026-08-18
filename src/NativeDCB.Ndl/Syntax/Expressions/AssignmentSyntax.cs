namespace NativeDCB.Ndl;

public sealed record AssignmentSyntax(string Name, ExpressionSyntax Value, TextSpan Span) : SyntaxNode(Span);