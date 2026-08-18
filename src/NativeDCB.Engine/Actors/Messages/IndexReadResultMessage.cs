namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record IndexReadResultMessage(
    [property: Id(id: 0)] bool Supported,
    [property: Id(id: 1)] long IndexHead,
    [property: Id(id: 2)] SequencedEventMessage[] Events);