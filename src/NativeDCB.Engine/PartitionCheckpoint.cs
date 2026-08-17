namespace NativeDCB.Engine;

public sealed record PartitionCheckpoint(
    int PartitionNumber,
    long? FirstEventId,
    long? LastEventId,
    int CommittedEventCount,
    long Length,
    string ContentFingerprint);