namespace NativeDCB.Model;

public sealed record PlanLocal(string Name, PlanExpression Value) : PlanEvaluationStep;