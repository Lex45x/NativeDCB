namespace NativeDCB.Ndl;

public sealed record DecisionSyntax(
    string Name,
    FromClauseSyntax From,
    IReadOnlyList<IncludeStageSyntax> Includes,
    EvaluateStageSyntax Evaluate,
    DecideStageSyntax Decide,
    TextSpan Span) : SyntaxNode(Span);