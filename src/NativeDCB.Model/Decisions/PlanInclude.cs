using NativeDCB.Model.Decisions.Expressions;

namespace NativeDCB.Model.Decisions;

public sealed record PlanInclude(
    string EventType,
    string Alias,
    PlanExpression Where,
    IReadOnlyList<PlanKeyBinding> KeyBindings,
    IReadOnlyList<PlanAssignment> Assignments);