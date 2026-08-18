using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax.Statements;

public abstract record EvaluateStatementSyntax(TextSpan Span) : SyntaxNode(Span);