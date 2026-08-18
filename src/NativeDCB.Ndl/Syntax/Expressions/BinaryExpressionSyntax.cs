using NativeDCB.Ndl.Lexing;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Expressions;

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxKind OperatorKind,
    ExpressionSyntax Right,
    TextSpan Span) : ExpressionSyntax(Span);