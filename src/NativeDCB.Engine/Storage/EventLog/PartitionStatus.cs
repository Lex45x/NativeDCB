// Persisted JSON contracts retain fields that are validated by external tooling.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine;

public sealed record PartitionStatus(
    int PartitionNumber,
    string FilePath,
    bool IsActive,
    long? FirstEventId,
    long? LastEventId,
    int CommittedEventCount,
    long Length);