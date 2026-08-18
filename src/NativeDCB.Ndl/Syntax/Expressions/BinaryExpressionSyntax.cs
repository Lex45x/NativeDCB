namespace NativeDCB.Ndl;

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxKind OperatorKind,
    ExpressionSyntax Right,
    TextSpan Span) : ExpressionSyntax(Span);