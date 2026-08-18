namespace NativeDCB.Ndl;

public sealed record ObjectExpressionSyntax(IReadOnlyList<AssignmentSyntax> Assignments, TextSpan Span)
    : ExpressionSyntax(Span);