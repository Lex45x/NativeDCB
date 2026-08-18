using NativeDCB.Model;

namespace NativeDCB.Engine;

public sealed record StateFileModel(
    int FormatVersion,
    Guid StoreId,
    int PartitionNumber,
    long Head,
    long CommittedEventCount,
    IReadOnlyList<SequencedEvent> Events,
    IReadOnlyList<long> KnownEventIds,
    IReadOnlyList<CommandState> Commands,
    IReadOnlyList<LatestEventState> LatestEvents,
    IReadOnlyList<PartitionCheckpoint> Partitions,
    string EventSnapshotFingerprint,
    string EventSchemaFingerprint,
    string KeyEncodingFingerprint,
    string StateSchemaFingerprint);