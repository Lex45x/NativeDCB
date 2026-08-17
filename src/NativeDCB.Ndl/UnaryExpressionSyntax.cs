namespace NativeDCB.Ndl;

public sealed record UnaryExpressionSyntax(SyntaxKind OperatorKind, ExpressionSyntax Operand, TextSpan Span)
    : ExpressionSyntax(Span);