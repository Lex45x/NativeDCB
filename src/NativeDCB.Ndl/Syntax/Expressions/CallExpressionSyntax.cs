using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record CallExpressionSyntax(
    ExpressionSyntax Target,
    IReadOnlyList<ExpressionSyntax> Arguments,
    TextSpan Span) : ExpressionSyntax(Span);