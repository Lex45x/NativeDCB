using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Statements;

public sealed record LetStatementSyntax(string Name, ExpressionSyntax Value, TextSpan Span)
    : EvaluateStatementSyntax(Span);