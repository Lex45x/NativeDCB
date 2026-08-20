using NativeDCB.Model.Databases;

// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record MainStatusMessage(
    [property: Id(0)] DatabaseStatus Status,
    [property: Id(1)] long Head,
    [property: Id(2)] int ActivePartition,
    [property: Id(3)] bool WriterLockOwned,
    [property: Id(4)] bool ReadAvailable,
    [property: Id(5)] bool WriteAvailable,
    [property: Id(6)] string? LastFault);

[GenerateSerializer]
[Immutable]
public sealed record StoreIdMessage([property: Id(0)] Guid StoreId);