using NativeDCB.Ndl.Syntax.Statements;
using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax;

public sealed record DecideStageSyntax(IReadOnlyList<EmitStatementSyntax> Emissions, TextSpan Span) : SyntaxNode(Span);