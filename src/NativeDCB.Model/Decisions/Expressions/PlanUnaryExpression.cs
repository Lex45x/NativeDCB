namespace NativeDCB.Model.Decisions.Expressions;

public sealed record PlanUnaryExpression(PlanUnaryOperator Operator, PlanExpression Operand) : PlanExpression;