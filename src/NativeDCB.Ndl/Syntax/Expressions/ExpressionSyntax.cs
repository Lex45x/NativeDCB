namespace NativeDCB.Ndl;

public abstract record ExpressionSyntax(TextSpan Span) : SyntaxNode(Span);