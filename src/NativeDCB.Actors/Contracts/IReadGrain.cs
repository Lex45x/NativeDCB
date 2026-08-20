using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IReadGrain : IGrainWithStringKey
{
    Task<long> CaptureHeadAsync(GrainCancellationToken cancellationToken);

    Task<PartitionListMessage> ListPartitionsAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadRangeAsync(
        long fromEventIdInclusive,
        long toEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventReadSnapshotMessage> ReadRangeSnapshotAsync(
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadByQueryAsync(
        EventQueryMessage query,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventReadSnapshotMessage> ReadQuerySnapshotAsync(
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadByCommandIdAsync(
        Guid commandId,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<CommandReadSnapshotMessage> ReadCommandSnapshotAsync(
        Guid commandId,
        GrainCancellationToken cancellationToken);
}