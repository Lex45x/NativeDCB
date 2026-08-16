using NativeDCB.Model;

// Orleans discovers grain implementations at runtime.
// ReSharper disable UnusedType.Global

namespace NativeDCB.Engine.Actors;

public interface IDatabaseStoreProvider
{
    Task<JsonEventStore> GetStoreAsync(string database, CancellationToken cancellationToken);

    string GetDirectory(string database);

    void ReportFault(string database, Exception exception);
}

public interface IEventBatchValidator
{
    EventBatch Validate(string database, EventBatch batch);
}

public sealed class MainWriterGrain(
    IDatabaseStoreProvider stores,
    IEventBatchValidator validator,
    IGrainFactory grains) : Grain, IMainWriterGrain
{
    public async Task<WriterStateMessage> GetStateAsync(GrainCancellationToken cancellationToken)
    {
        JsonEventStore store = await stores.GetStoreAsync(
                this.GetPrimaryKeyString(), cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        await NotifyStateBuilderAsync(store.ActivePartition)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new WriterStateMessage(store.Head, store.ActivePartition);
    }

    public async Task<AppendResultMessage> AppendAsync(
        EventBatchMessage batch,
        AppendConditionMessage condition,
        GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        try
        {
            JsonEventStore store = await stores.GetStoreAsync(database, cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            EventBatch validatedBatch = validator.Validate(database, ActorMessageMapper.ToModel(batch));
            AppendResult result = await store.AppendAsync(
                    validatedBatch,
                    ActorMessageMapper.ToModel(condition),
                    cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (store.IsFaulted)
            {
                stores.ReportFault(database, new EventStoreUnavailableException(
                    store.LastFault ?? "The event store faulted after committing the batch."));
            }

            if (result.Outcome == AppendOutcome.Committed)
            {
                await grains.GetGrain<IIndexCoordinatorGrain>(database).ObserveAsync(
                        new EventListMessage(result.Events.Select(ActorMessageMapper.ToMessage).ToArray()),
                        result.Head)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            await NotifyStateBuilderAsync(store.ActivePartition)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return ActorMessageMapper.ToMessage(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            stores.ReportFault(database, exception);
            throw;
        }
    }

    private Task NotifyStateBuilderAsync(int activePartition)
    {
        return activePartition <= 1
            ? Task.CompletedTask
            : grains.GetGrain<IStateBuilderGrain>(this.GetPrimaryKeyString()).BuildMissingAsync(activePartition);
    }
}

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

public sealed class StateBuilderGrain(IDatabaseStoreProvider stores) : Grain, IStateBuilderGrain
{
    public async Task BuildMissingAsync(int activePartition)
    {
        if (activePartition <= 1)
        {
            return;
        }

        string directory = stores.GetDirectory(this.GetPrimaryKeyString());
        StateBuilder builder = new(new DatabaseOptions(directory));
        for (int partition = 1; partition < activePartition; partition++)
        {
            await builder.BuildAsync(partition)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public async Task RebuildAsync(int partitionNumber)
    {
        string directory = stores.GetDirectory(this.GetPrimaryKeyString());
        StateBuilder builder = new(new DatabaseOptions(directory));
        await builder.BuildAsync(partitionNumber)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }
}