using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record ObjectExpressionSyntax(IReadOnlyList<AssignmentSyntax> Assignments, TextSpan Span)
    : ExpressionSyntax(Span);