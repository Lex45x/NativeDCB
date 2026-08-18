using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Actors.Mapping;
using NativeDCB.Engine.Actors.Messages;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;

// Orleans discovers grain implementations at runtime.
// ReSharper disable UnusedType.Global

namespace NativeDCB.Engine.Actors.Grains;

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