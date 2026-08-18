using NativeDCB.Model.Queries;

namespace NativeDCB.Model.Events.Appending;

public sealed record AppendCondition(EventQuery Query, long AfterEventId);