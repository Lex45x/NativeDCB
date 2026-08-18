using NativeDCB.Ndl.Text;

namespace NativeDCB.Ndl.Syntax;

public sealed record FromClauseSyntax(string CommandType, string Alias, TextSpan Span) : SyntaxNode(Span);