using NativeDCB.Ndl.Syntax.Statements;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax;

public sealed record EvaluateStageSyntax(IReadOnlyList<EvaluateStatementSyntax> Statements, TextSpan Span)
    : SyntaxNode(Span);