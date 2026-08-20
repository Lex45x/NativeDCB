namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record IndexSnapshotMessage(
    [property: Id(id: 0)] bool Supported,
    [property: Id(id: 1)] long Head,
    [property: Id(id: 2)] SequencedEventMessage[] Events,
    [property: Id(id: 3)] string? Fault);