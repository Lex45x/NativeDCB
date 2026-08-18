using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record MemberAccessExpressionSyntax(ExpressionSyntax Target, string Member, TextSpan Span)
    : ExpressionSyntax(Span);