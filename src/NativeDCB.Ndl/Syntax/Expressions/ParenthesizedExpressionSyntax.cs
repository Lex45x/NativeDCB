using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record ParenthesizedExpressionSyntax(ExpressionSyntax Expression, TextSpan Span) : ExpressionSyntax(Span);