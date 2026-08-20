namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record EventListMessage(
    [property: Id(id: 0)] SequencedEventMessage[] Events);