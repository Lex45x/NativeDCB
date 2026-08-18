namespace NativeDCB.Ndl;

public sealed record DecideStageSyntax(IReadOnlyList<EmitStatementSyntax> Emissions, TextSpan Span) : SyntaxNode(Span);