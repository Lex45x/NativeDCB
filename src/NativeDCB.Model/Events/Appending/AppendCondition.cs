namespace NativeDCB.Model;

public sealed record AppendCondition(EventQuery Query, long AfterEventId);