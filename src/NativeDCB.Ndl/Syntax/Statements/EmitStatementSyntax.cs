using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Statements;

public sealed record EmitStatementSyntax(string EventType, ObjectExpressionSyntax Value, TextSpan Span)
    : SyntaxNode(Span);