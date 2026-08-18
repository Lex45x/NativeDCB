namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanLiteralExpression(PlanLiteralKind Kind, string? Value) : PlanExpression;