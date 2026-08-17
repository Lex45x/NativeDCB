namespace NativeDCB.Engine;

internal sealed record RecoveredPartition(
    int Number,
    string Path,
    long? FirstEventId,
    long? LastEventId,
    int EventCount);