using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Statements;

public sealed record RequireStatementSyntax(ExpressionSyntax Condition, ExpressionSyntax Reason, TextSpan Span)
    : EvaluateStatementSyntax(Span);