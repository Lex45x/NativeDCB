namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record EventBatchMessage(
    [property: Id(id: 0)] Guid CommandId,
    [property: Id(id: 1)] string CommandType,
    [property: Id(id: 2)] CandidateEventMessage[] Events);