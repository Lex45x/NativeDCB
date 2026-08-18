namespace NativeDCB.Ndl;

public sealed record FromClauseSyntax(string CommandType, string Alias, TextSpan Span) : SyntaxNode(Span);