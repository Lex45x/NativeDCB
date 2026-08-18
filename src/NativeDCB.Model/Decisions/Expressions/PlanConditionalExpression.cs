namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanConditionalExpression(
    PlanExpression Condition,
    PlanExpression WhenTrue,
    PlanExpression WhenFalse) : PlanExpression;