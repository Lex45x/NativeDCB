using NativeDCB.Model;

namespace NativeDCB.Engine.Actors;

public sealed class ReadGrain(IDatabaseStoreProvider stores) : Grain, IReadGrain
{
    public async Task<PartitionListMessage> ListPartitionsAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<PartitionStatus> partitions = await PartitionEventReader.ListPartitionStatusAsync(
                stores.GetDirectory(this.GetPrimaryKeyString()),
                throughEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new PartitionListMessage(partitions.Select(value => new PartitionStatusMessage(
            value.PartitionNumber,
            value.FilePath,
            value.IsActive,
            value.FirstEventId,
            value.LastEventId,
            value.CommittedEventCount,
            value.Length)).ToArray());
    }

    public async Task<EventListMessage> ReadRangeAsync(
        long fromEventIdInclusive,
        long toEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<SequencedEvent> events = await PartitionEventReader.ReadRangeAsync(
                stores.GetDirectory(this.GetPrimaryKeyString()),
                fromEventIdInclusive,
                toEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(events);
    }

    public async Task<EventListMessage> ReadByQueryAsync(
        EventQueryMessage query,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<SequencedEvent> events = await PartitionEventReader.ReadByQueryAsync(
                stores.GetDirectory(this.GetPrimaryKeyString()),
                ActorMessageMapper.ToModel(query),
                throughEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(events);
    }

    public async Task<EventListMessage> ReadByCommandIdAsync(
        Guid commandId,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<SequencedEvent> events = await PartitionEventReader.ReadByCommandIdAsync(
                stores.GetDirectory(this.GetPrimaryKeyString()),
                commandId,
                throughEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(events);
    }

    private static EventListMessage ToMessage(IEnumerable<SequencedEvent> events)
    {
        return new EventListMessage(
            events.Select(ActorMessageMapper.ToMessage).ToArray());
    }
}