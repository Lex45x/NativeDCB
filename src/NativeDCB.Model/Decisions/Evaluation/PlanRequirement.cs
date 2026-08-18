using NativeDCB.Model.Decisions.Expressions;

namespace NativeDCB.Model.Decisions.Evaluation;

public sealed record PlanRequirement(PlanExpression Condition, PlanExpression Reason) : PlanEvaluationStep;