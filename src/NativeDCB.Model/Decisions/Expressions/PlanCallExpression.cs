namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanCallExpression(string Function, IReadOnlyList<PlanExpression> Arguments) : PlanExpression;