using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax;

public sealed record DocumentSyntax(IReadOnlyList<DecisionSyntax> Decisions, TextSpan Span) : SyntaxNode(Span);