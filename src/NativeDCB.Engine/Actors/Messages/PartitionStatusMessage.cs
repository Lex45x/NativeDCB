// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine.Actors;

[GenerateSerializer]
[Immutable]
public sealed record PartitionStatusMessage(
    [property: Id(id: 0)] int PartitionNumber,
    [property: Id(id: 1)] string FilePath,
    [property: Id(id: 2)] bool IsActive,
    [property: Id(id: 3)] long? FirstEventId,
    [property: Id(id: 4)] long? LastEventId,
    [property: Id(id: 5)] int CommittedEventCount,
    [property: Id(id: 6)] long Length);