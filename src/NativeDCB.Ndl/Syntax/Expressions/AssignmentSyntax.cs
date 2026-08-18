using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record AssignmentSyntax(string Name, ExpressionSyntax Value, TextSpan Span) : SyntaxNode(Span);