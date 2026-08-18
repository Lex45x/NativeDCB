namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record DecisionResultMessage(
    [property: Id(id: 0)] DecisionResultOutcome Outcome,
    [property: Id(id: 1)] Guid CommandId,
    [property: Id(id: 2)] string CommandType,
    [property: Id(id: 3)] SequencedEventMessage[] Events,
    [property: Id(id: 4)] string? Code,
    [property: Id(id: 5)] string? Message);