namespace NativeDCB.Ndl;

public sealed record DocumentSyntax(IReadOnlyList<DecisionSyntax> Decisions, TextSpan Span) : SyntaxNode(Span);