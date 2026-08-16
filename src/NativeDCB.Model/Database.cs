// Public model contracts are consumed by external clients.
// ReSharper disable UnusedType.Global
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Model;

public enum DatabaseStatus
{
    Discovered,
    AcquiringLock,
    Recovering,
    Ready,
    Draining,
    Stopped,
    Faulted
}

public sealed record DatabaseInfo(
    string Name,
    DatabaseStatus Status,
    long Head,
    int ActivePartition,
    bool WriterLockOwned,
    string? Fault = null);

public sealed record HandlerRegistration(
    string Name,
    string CommandType,
    string NdlSource,
    string SourceFingerprint,
    string PlanFingerprint);