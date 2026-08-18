using NativeDCB.Model.Decisions.Expressions;

namespace NativeDCB.Model.Decisions;

public sealed record PlanAssignment(string Name, PlanExpression Value);