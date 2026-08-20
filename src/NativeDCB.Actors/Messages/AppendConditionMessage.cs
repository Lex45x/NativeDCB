namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record AppendConditionMessage(
    [property: Id(id: 0)] EventQueryMessage Query,
    [property: Id(id: 1)] long AfterEventId);