// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine.Actors;

[GenerateSerializer]
[Immutable]
public sealed record AppendResultMessage(
    [property: Id(id: 0)] AppendResultOutcome Outcome,
    [property: Id(id: 1)] Guid CommandId,
    [property: Id(id: 2)] SequencedEventMessage[] Events,
    [property: Id(id: 3)] long Head);