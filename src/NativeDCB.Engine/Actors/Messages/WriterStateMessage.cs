// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record WriterStateMessage(
    [property: Id(id: 0)] long Head,
    [property: Id(id: 1)] int ActivePartition);