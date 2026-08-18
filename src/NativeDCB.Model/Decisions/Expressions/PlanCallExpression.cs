namespace NativeDCB.Model;

public sealed record PlanCallExpression(string Function, IReadOnlyList<PlanExpression> Arguments) : PlanExpression;