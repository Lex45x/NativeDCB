namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanMemberExpression(PlanExpression Target, string Member) : PlanExpression;