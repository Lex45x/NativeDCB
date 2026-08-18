using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record IdentifierExpressionSyntax(string Name, TextSpan Span) : ExpressionSyntax(Span);