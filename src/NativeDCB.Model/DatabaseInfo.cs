// Public model contracts are consumed by external clients.
// ReSharper disable UnusedType.Global
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Model;

public sealed record DatabaseInfo(
    string Name,
    DatabaseStatus Status,
    long Head,
    int ActivePartition,
    bool WriterLockOwned,
    string? Fault = null);