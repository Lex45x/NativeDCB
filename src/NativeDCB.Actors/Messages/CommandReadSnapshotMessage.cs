namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record CommandReadSnapshotMessage(
    [property: Id(0)] Guid StoreId,
    [property: Id(1)] long Head,
    [property: Id(2)] SequencedEventMessage[] Events);