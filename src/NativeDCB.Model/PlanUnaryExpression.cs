namespace NativeDCB.Model;

public sealed record PlanUnaryExpression(PlanUnaryOperator Operator, PlanExpression Operand) : PlanExpression;