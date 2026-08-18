using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public abstract record ExpressionSyntax(TextSpan Span) : SyntaxNode(Span);