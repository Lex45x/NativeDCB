namespace NativeDCB.Model;

public sealed record PlanConditionalExpression(
    PlanExpression Condition,
    PlanExpression WhenTrue,
    PlanExpression WhenFalse) : PlanExpression;