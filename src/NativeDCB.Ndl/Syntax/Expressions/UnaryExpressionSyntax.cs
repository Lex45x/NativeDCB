using NativeDCB.Ndl.Lexing;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record UnaryExpressionSyntax(SyntaxKind OperatorKind, ExpressionSyntax Operand, TextSpan Span)
    : ExpressionSyntax(Span);