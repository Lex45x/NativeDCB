namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record IndexQueryResultMessage(
    [property: Id(id: 0)] long ObservedHead,
    [property: Id(id: 1)] SequencedEventMessage[] Events);