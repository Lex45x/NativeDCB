using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model;
using NativeDCB.Model.Events;

using Orleans.Concurrency;

namespace NativeDCB.Actors.Grains;

[StatelessWorker]
// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class ReadGrain(ActorStoragePath storage) : Grain, IReadGrain
{
    public Task<long> CaptureHeadAsync(GrainCancellationToken cancellationToken)
    {
        return PartitionEventReader.GetHeadAsync(
            Directory(), cancellationToken.CancellationToken);
    }

    public async Task<PartitionListMessage> ListPartitionsAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<PartitionStatus> partitions = await PartitionEventReader.ListPartitionStatusAsync(
                Directory(),
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
                Directory(),
                fromEventIdInclusive,
                toEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(events);
    }

    public async Task<EventReadSnapshotMessage> ReadRangeSnapshotAsync(
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        EventReadSnapshot snapshot = await PartitionEventReader.ReadRangeSnapshotAsync(
                Directory(),
                afterEventIdExclusive,
                throughEventIdInclusive,
                maxCount,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(snapshot);
    }

    public async Task<EventListMessage> ReadByQueryAsync(
        EventQueryMessage query,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<SequencedEvent> events = await PartitionEventReader.ReadByQueryAsync(
                Directory(),
                ActorMessageMapper.ToModel(query),
                throughEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(events);
    }

    public async Task<EventReadSnapshotMessage> ReadQuerySnapshotAsync(
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        EventReadSnapshot snapshot = await PartitionEventReader.ReadQuerySnapshotAsync(
                Directory(),
                ActorMessageMapper.ToModel(query),
                afterEventIdExclusive,
                throughEventIdInclusive,
                maxCount,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(snapshot);
    }

    public async Task<EventListMessage> ReadByCommandIdAsync(
        Guid commandId,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        IReadOnlyList<SequencedEvent> events = await PartitionEventReader.ReadByCommandIdAsync(
                Directory(),
                commandId,
                throughEventIdInclusive,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(events);
    }

    public async Task<CommandReadSnapshotMessage> ReadCommandSnapshotAsync(
        Guid commandId,
        GrainCancellationToken cancellationToken)
    {
        CommandEventSnapshot snapshot = await PartitionEventReader.ReadCommandSnapshotAsync(
                Directory(),
                commandId,
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new CommandReadSnapshotMessage(
            snapshot.StoreId,
            snapshot.Head,
            snapshot.Events.Select(ActorMessageMapper.ToMessage).ToArray());
    }

    private static EventListMessage ToMessage(IEnumerable<SequencedEvent> events)
    {
        return new EventListMessage(
            events.Select(ActorMessageMapper.ToMessage).ToArray());
    }

    private static EventReadSnapshotMessage ToMessage(EventReadSnapshot snapshot)
    {
        return new EventReadSnapshotMessage(
            snapshot.ObservedHead,
            snapshot.Events.Select(ActorMessageMapper.ToMessage).ToArray());
    }

    private string Directory()
    {
        return storage.GetDatabaseDirectory(this.GetPrimaryKeyString());
    }
}