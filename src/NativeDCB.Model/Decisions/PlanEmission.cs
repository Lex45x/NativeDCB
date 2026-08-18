namespace NativeDCB.Model.Decisions;

public sealed record PlanEmission(string EventType, IReadOnlyList<PlanAssignment> Assignments);