using NativeDCB.Ndl.Syntax.Expressions;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax;

public sealed record IncludeStageSyntax(
    string EventType,
    string Alias,
    ExpressionSyntax Where,
    ObjectExpressionSyntax Apply,
    TextSpan Span) : SyntaxNode(Span);