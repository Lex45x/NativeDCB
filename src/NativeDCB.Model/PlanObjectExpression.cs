namespace NativeDCB.Model;

public sealed record PlanObjectExpression(IReadOnlyList<PlanAssignment> Assignments) : PlanExpression;