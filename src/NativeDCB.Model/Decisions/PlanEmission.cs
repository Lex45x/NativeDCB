namespace NativeDCB.Model;

public sealed record PlanEmission(string EventType, IReadOnlyList<PlanAssignment> Assignments);