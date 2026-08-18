namespace NativeDCB.Model;

public sealed record PlanLiteralExpression(PlanLiteralKind Kind, string? Value) : PlanExpression;