using NativeDCB.Engine.Actors.Messages;

namespace NativeDCB.Engine.Actors.Contracts;

public interface IReadGrain : IGrainWithStringKey
{
    Task<PartitionListMessage> ListPartitionsAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadRangeAsync(
        long fromEventIdInclusive,
        long toEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadByQueryAsync(
        EventQueryMessage query,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadByCommandIdAsync(
        Guid commandId,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);
}