using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record LiteralExpressionSyntax(LiteralKind Kind, object? Value, string Text, TextSpan Span)
    : ExpressionSyntax(Span);