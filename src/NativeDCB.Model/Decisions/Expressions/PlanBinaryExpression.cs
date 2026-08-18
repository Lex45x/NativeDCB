namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanBinaryExpression(
    PlanExpression Left,
    PlanBinaryOperator Operator,
    PlanExpression Right) : PlanExpression;