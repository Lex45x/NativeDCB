using System.Text.Json;

using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Actors.Mapping;
using NativeDCB.Engine.Actors.Messages;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.Indexes;
using NativeDCB.Engine.Storage.State;
using NativeDCB.Model;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.Engine.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class IndexGrain(IDatabaseStoreProvider stores) : Grain, IIndexGrain
{
    private bool _hydrating;
    private string? _lastFault;
    private bool _loaded;
    private IndexFileModel? _model;

    public async Task ObserveAsync(EventListMessage events, long head)
    {
        try
        {
            await EnsureLoadedAsync()
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (_model is null)
            {
                await RebuildCoreAsync(head)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                return;
            }

            IndexIdentity identity = GetIdentity();
            SequencedEvent[] matching = events.Events
                .Where(value => value.EventId > _model.Head && Matches(value, identity))
                .Select(ActorMessageMapper.ToModel)
                .ToArray();
            _model = _model with { Head = Math.Max(_model.Head, head), Events = [.. _model.Events, .. matching] };
            await PersistAsync()
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _lastFault = exception.Message;
        }
    }

    public async Task RebuildAsync(long throughEventIdInclusive)
    {
        try
        {
            await RebuildCoreAsync(throughEventIdInclusive)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _lastFault = exception.Message;
        }
    }

    public async Task<IndexStatusMessage> GetStatusAsync(
        long mainHead,
        GrainCancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        IndexIdentity identity = GetIdentity();
        string path = GetPath(identity);
        return new IndexStatusMessage(
            identity.EventType,
            identity.Key,
            path,
            _model?.Head ?? 0,
            mainHead,
            _lastFault is not null
                ? IndexHydrationState.Faulted
                : _hydrating
                    ? IndexHydrationState.Hydrating
                    : _model is null
                        ? IndexHydrationState.Pending
                        : IndexHydrationState.Ready,
            _lastFault);
    }

    public async Task<EventListMessage> ReadAsync(GrainCancellationToken cancellationToken)
    {
        await EnsureLoadedAsync(cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new EventListMessage((_model?.Events ?? [])
            .Select(ActorMessageMapper.ToMessage)
            .ToArray());
    }

    private async Task RebuildCoreAsync(long throughEventIdInclusive)
    {
        _hydrating = true;
        try
        {
            IndexIdentity identity = GetIdentity();
            EventQuery query = new([
                new QueryItem(
                    [identity.EventType],
                    [new EventKey(identity.Key.Name, identity.Key.Value)])
            ]);
            IReadOnlyList<SequencedEvent> events = await PartitionEventReader.ReadByQueryAsync(
                    stores.GetDirectory(identity.Database),
                    query,
                    throughEventIdInclusive,
                    CancellationToken.None)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            _model = new IndexFileModel(
                FormatVersion: 1,
                identity.EventType,
                new EventKey(identity.Key.Name, identity.Key.Value),
                throughEventIdInclusive,
                events,
                StateBuilder.EventSchemaFingerprint,
                StateBuilder.KeyEncodingFingerprint);
            _loaded = true;
            _lastFault = null;
            await PersistAsync()
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        finally
        {
            _hydrating = false;
        }
    }

    private async Task EnsureLoadedAsync(CancellationToken cancellationToken = default)
    {
        if (_loaded)
        {
            return;
        }

        IndexIdentity identity = GetIdentity();
        try
        {
            IndexFileModel? model = await IndexFileStore.TryReadAsync(
                    GetPath(identity), cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (model is not null &&
                model.FormatVersion == 1 &&
                string.Equals(model.EventType, identity.EventType, StringComparison.Ordinal) &&
                model.Key == new EventKey(identity.Key.Name, identity.Key.Value) &&
                string.Equals(model.EventSchemaFingerprint, StateBuilder.EventSchemaFingerprint,
                    StringComparison.Ordinal) &&
                string.Equals(model.KeyEncodingFingerprint, StateBuilder.KeyEncodingFingerprint,
                    StringComparison.Ordinal))
            {
                _model = model;
                _lastFault = null;
            }
            else if (model is not null)
            {
                _lastFault = "The index file metadata or schema fingerprints are invalid.";
            }
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            _lastFault = exception.Message;
        }

        _loaded = true;
    }

    private Task PersistAsync()
    {
        IndexIdentity identity = GetIdentity();
        return IndexFileStore.WriteAsync(GetPath(identity), _model!, CancellationToken.None);
    }

    private IndexIdentity GetIdentity()
    {
        return IndexIdentity.Decode(this.GetPrimaryKeyString());
    }

    private string GetPath(IndexIdentity identity)
    {
        return IndexFileStore.GetPath(
            stores.GetDirectory(identity.Database),
            identity.EventType,
            new EventKey(identity.Key.Name, identity.Key.Value));
    }

    private static bool Matches(SequencedEventMessage value, IndexIdentity identity)
    {
        return string.Equals(value.Type, identity.EventType, StringComparison.Ordinal) &&
               value.Keys.Contains(identity.Key);
    }
}