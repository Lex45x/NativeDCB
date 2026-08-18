namespace NativeDCB.Ndl;

public abstract record EvaluateStatementSyntax(TextSpan Span) : SyntaxNode(Span);