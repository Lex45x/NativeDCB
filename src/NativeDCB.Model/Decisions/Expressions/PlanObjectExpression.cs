namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanObjectExpression(IReadOnlyList<PlanAssignment> Assignments) : PlanExpression;