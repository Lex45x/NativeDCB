namespace NativeDCB.Model;

public sealed record PlanBinaryExpression(
    PlanExpression Left,
    PlanBinaryOperator Operator,
    PlanExpression Right) : PlanExpression;