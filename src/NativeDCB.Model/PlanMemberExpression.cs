namespace NativeDCB.Model;

public sealed record PlanMemberExpression(PlanExpression Target, string Member) : PlanExpression;