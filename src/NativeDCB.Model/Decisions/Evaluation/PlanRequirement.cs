namespace NativeDCB.Model;

public sealed record PlanRequirement(PlanExpression Condition, PlanExpression Reason) : PlanEvaluationStep;