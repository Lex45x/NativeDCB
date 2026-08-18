using NativeDCB.Model.Decisions.Expressions;

namespace NativeDCB.Model.Decisions.Evaluation;

public sealed record PlanLocal(string Name, PlanExpression Value) : PlanEvaluationStep;