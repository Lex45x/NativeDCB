namespace NativeDCB.Ndl;

public abstract record SyntaxNode(TextSpan Span);

public sealed record DocumentSyntax(IReadOnlyList<DecisionSyntax> Decisions, TextSpan Span) : SyntaxNode(Span);

public sealed record DecisionSyntax(
    string Name,
    FromClauseSyntax From,
    IReadOnlyList<IncludeStageSyntax> Includes,
    EvaluateStageSyntax Evaluate,
    DecideStageSyntax Decide,
    TextSpan Span) : SyntaxNode(Span);

public sealed record FromClauseSyntax(string CommandType, string Alias, TextSpan Span) : SyntaxNode(Span);

public sealed record IncludeStageSyntax(
    string EventType,
    string Alias,
    ExpressionSyntax Where,
    ObjectExpressionSyntax Apply,
    TextSpan Span) : SyntaxNode(Span);

public sealed record EvaluateStageSyntax(IReadOnlyList<EvaluateStatementSyntax> Statements, TextSpan Span)
    : SyntaxNode(Span);

public abstract record EvaluateStatementSyntax(TextSpan Span) : SyntaxNode(Span);

public sealed record RequireStatementSyntax(ExpressionSyntax Condition, ExpressionSyntax Reason, TextSpan Span)
    : EvaluateStatementSyntax(Span);

public sealed record LetStatementSyntax(string Name, ExpressionSyntax Value, TextSpan Span)
    : EvaluateStatementSyntax(Span);

public sealed record DecideStageSyntax(IReadOnlyList<EmitStatementSyntax> Emissions, TextSpan Span) : SyntaxNode(Span);

public sealed record EmitStatementSyntax(string EventType, ObjectExpressionSyntax Value, TextSpan Span)
    : SyntaxNode(Span);

public abstract record ExpressionSyntax(TextSpan Span) : SyntaxNode(Span);

public enum LiteralKind
{
    Null,
    Boolean,
    Integer,
    Decimal,
    String,
    Guid,
    DateTime,
    Duration
}

public sealed record LiteralExpressionSyntax(LiteralKind Kind, object? Value, string Text, TextSpan Span)
    : ExpressionSyntax(Span);

public sealed record IdentifierExpressionSyntax(string Name, TextSpan Span) : ExpressionSyntax(Span);

public sealed record MemberAccessExpressionSyntax(ExpressionSyntax Target, string Member, TextSpan Span)
    : ExpressionSyntax(Span);

public sealed record CallExpressionSyntax(
    ExpressionSyntax Target,
    IReadOnlyList<ExpressionSyntax> Arguments,
    TextSpan Span) : ExpressionSyntax(Span);

public sealed record ParenthesizedExpressionSyntax(ExpressionSyntax Expression, TextSpan Span) : ExpressionSyntax(Span);

public sealed record UnaryExpressionSyntax(SyntaxKind OperatorKind, ExpressionSyntax Operand, TextSpan Span)
    : ExpressionSyntax(Span);

public sealed record BinaryExpressionSyntax(
    ExpressionSyntax Left,
    SyntaxKind OperatorKind,
    ExpressionSyntax Right,
    TextSpan Span) : ExpressionSyntax(Span);

public sealed record ConditionalExpressionSyntax(
    ExpressionSyntax Condition,
    ExpressionSyntax WhenTrue,
    ExpressionSyntax WhenFalse,
    TextSpan Span) : ExpressionSyntax(Span);

public sealed record ObjectExpressionSyntax(IReadOnlyList<AssignmentSyntax> Assignments, TextSpan Span)
    : ExpressionSyntax(Span);

public sealed record AssignmentSyntax(string Name, ExpressionSyntax Value, TextSpan Span) : SyntaxNode(Span);