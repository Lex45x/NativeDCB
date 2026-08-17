namespace NativeDCB.Model;

public sealed record PlanInclude(
    string EventType,
    string Alias,
    PlanExpression Where,
    IReadOnlyList<PlanKeyBinding> KeyBindings,
    IReadOnlyList<PlanAssignment> Assignments);