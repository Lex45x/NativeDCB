using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model.Databases;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;

// Orleans discovers grain implementations at runtime.
// ReSharper disable UnusedType.Global

namespace NativeDCB.Actors.Grains;

public sealed class MainWriterGrain(ActorStoragePath storage, IGrainFactory grains) : Grain, IMainWriterGrain
{
    private string? _lastFault;
    private JsonEventStore? _store;
    private DatabaseStatus _status = DatabaseStatus.Discovered;

    public async Task<WriterStateMessage> CreateAsync(GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        string directory = storage.GetNewDatabaseDirectory(database);
        if (_store is not null || Directory.Exists(directory))
        {
            throw new InvalidOperationException($"Database '{database}' already exists.");
        }

        _status = DatabaseStatus.AcquiringLock;
        try
        {
            _status = DatabaseStatus.Recovering;
            _store = await JsonEventStore.CreateAsync(
                    Options(directory), cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            _lastFault = null;
            _status = DatabaseStatus.Ready;
            return State(_store);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MarkFault(exception);
            throw;
        }
    }

    public Task<MainStatusMessage> GetStatusAsync(GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        string directory = storage.GetDatabaseDirectory(this.GetPrimaryKeyString());
        JsonEventStore? store = _store;
        return Task.FromResult(new MainStatusMessage(
            _status,
            store?.Head ?? 0,
            store?.ActivePartition ?? 0,
            WriterLockOwned: store is not null,
            ReadAvailable: File.Exists(Path.Combine(directory, "store_partition_000001_v1.json")),
            WriteAvailable: _status == DatabaseStatus.Ready && store is { IsFaulted: false },
            _lastFault ?? store?.LastFault));
    }

    public async Task<WriterStateMessage> GetStateAsync(GrainCancellationToken cancellationToken)
    {
        JsonEventStore store = await OpenAsync(cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        await NotifyStateBuilderAsync(store.ActivePartition)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return State(store);
    }

    public async Task<StoreIdMessage> GetStoreIdAsync(GrainCancellationToken cancellationToken)
    {
        JsonEventStore store = await OpenAsync(cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new StoreIdMessage(store.StoreId);
    }

    public async Task<AppendResultMessage> AppendAsync(
        EventBatchMessage batch,
        AppendConditionMessage condition,
        GrainCancellationToken cancellationToken)
    {
        try
        {
            JsonEventStore store = await OpenAsync(cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            AppendResult result = await store.AppendAsync(
                    ActorMessageMapper.ToModel(batch),
                    ActorMessageMapper.ToModel(condition),
                    cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (store.IsFaulted)
            {
                MarkFault(new EventStoreUnavailableException(
                    store.LastFault ?? "The event store faulted after committing the batch."));
            }

            if (result.Outcome == AppendOutcome.Committed)
            {
                EventListMessage events = new(result.Events.Select(ActorMessageMapper.ToMessage).ToArray());
                await NotifyIndexesAsync(events, result.Head)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                await NotifyStateBuilderAsync(store.ActivePartition)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            }

            return ActorMessageMapper.ToMessage(result);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                                                   EventStoreUnavailableException)
        {
            MarkFault(exception);
            throw;
        }
    }

    public override async Task OnDeactivateAsync(
        DeactivationReason reason,
        CancellationToken cancellationToken)
    {
        _status = DatabaseStatus.Draining;
        if (_store is not null)
        {
            await _store.DisposeAsync().ConfigureAwait(continueOnCapturedContext: true);
            _store = null;
        }

        _status = DatabaseStatus.Stopped;
        await base.OnDeactivateAsync(reason, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    private async Task<JsonEventStore> OpenAsync(CancellationToken cancellationToken)
    {
        if (_store is not null)
        {
            return _store;
        }

        string directory = storage.GetDatabaseDirectory(this.GetPrimaryKeyString());
        _status = DatabaseStatus.AcquiringLock;
        try
        {
            _status = DatabaseStatus.Recovering;
            _store = await JsonEventStore.OpenAsync(Options(directory), cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            _lastFault = null;
            _status = DatabaseStatus.Ready;
            return _store;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            MarkFault(exception);
            throw;
        }
    }

    private async Task NotifyIndexesAsync(
        EventListMessage events,
        long head)
    {
        string database = this.GetPrimaryKeyString();
        string[] identities = events.Events
            .SelectMany(@event => @event.Keys.Select(key => IndexIdentity.Encode(database, @event.Type, key)))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        await Task.WhenAll(identities.Select(identity =>
                grains.GetGrain<IIndexGrain>(identity).AdvanceAsync(head)))
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    private Task NotifyStateBuilderAsync(int activePartition)
    {
        return activePartition <= 1
            ? Task.CompletedTask
            : grains.GetGrain<IStateBuilderGrain>(this.GetPrimaryKeyString()).BuildMissingAsync(activePartition);
    }

    private DatabaseOptions Options(string directory)
    {
        return new DatabaseOptions(directory)
        {
            MaxEventCountPerPartition = storage.MaxEventCountPerPartition
        };
    }

    private void MarkFault(Exception exception)
    {
        _lastFault = exception.Message;
        _status = DatabaseStatus.Faulted;
    }

    private static WriterStateMessage State(JsonEventStore store)
    {
        return new WriterStateMessage(store.Head, store.ActivePartition);
    }
}