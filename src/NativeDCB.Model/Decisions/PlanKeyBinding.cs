using NativeDCB.Model.Decisions.Expressions;

namespace NativeDCB.Model.Decisions;

public sealed record PlanKeyBinding(string PropertyName, PlanExpression Value);