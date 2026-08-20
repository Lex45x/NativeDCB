using System.Text.Json;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.Indexes;
using NativeDCB.Engine.Storage.State;
using NativeDCB.Model;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class IndexGrain(ActorStoragePath storage, IGrainFactory grains) : Grain, IIndexGrain
{
    private bool _hydrating;
    private string? _lastFault;
    private bool _loaded;
    private IndexFileModel? _model;

    public async Task AdvanceAsync(long throughEventIdInclusive)
    {
        using GrainCancellationTokenSource cancellation = new();
        GrainCancellationToken cancellationToken = cancellation.Token;
        try
        {
            await EnsureLoadedAsync(cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (_model is null)
            {
                await RebuildCoreAsync(throughEventIdInclusive, cancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                return;
            }

            if (throughEventIdInclusive <= _model.Head)
            {
                return;
            }

            IndexIdentity identity = GetIdentity();
            EventListMessage tail = await grains.GetGrain<IReadGrain>(identity.Database)
                .ReadRangeAsync(_model.Head + 1, throughEventIdInclusive, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            SequencedEvent[] matching = tail.Events
                .Where(value => Matches(value, identity))
                .Select(ActorMessageMapper.ToModel)
                .ToArray();
            IndexFileModel next = _model with
            {
                Head = throughEventIdInclusive,
                Events = [.. _model.Events, .. matching]
            };
            await PersistAsync(next, cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            _model = next;
            _lastFault = null;
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                                    UnauthorizedAccessException)
        {
            _lastFault = exception.Message;
        }
    }

    public async Task RebuildAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
    {
        try
        {
            await RebuildCoreAsync(throughEventIdInclusive, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                                    UnauthorizedAccessException)
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
        string path = GetManifestPath(identity);
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

    private async Task RebuildCoreAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken)
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
            EventListMessage snapshot = await grains.GetGrain<IReadGrain>(identity.Database)
                .ReadByQueryAsync(
                    ActorMessageMapper.ToMessage(query),
                    throughEventIdInclusive,
                    cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            IReadOnlyList<SequencedEvent> events = snapshot.Events
                .Select(ActorMessageMapper.ToModel)
                .ToArray();
            IndexFileModel rebuilt = new(
                FormatVersion: 1,
                identity.EventType,
                new EventKey(identity.Key.Name, identity.Key.Value),
                throughEventIdInclusive,
                events,
                StateBuilder.EventSchemaFingerprint,
                StateBuilder.KeyEncodingFingerprint);
            await PersistAsync(rebuilt, cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            _model = rebuilt;
            _loaded = true;
            _lastFault = null;
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
            IndexFileModel? model = await IndexFileStore.TryReadPublishedAsync(
                    storage.GetDatabaseDirectory(identity.Database),
                    identity.EventType,
                    new EventKey(identity.Key.Name, identity.Key.Value),
                    cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (model is not null)
            {
                _model = model;
                _lastFault = null;
            }
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                                    UnauthorizedAccessException)
        {
            _lastFault = exception.Message;
        }

        _loaded = true;
    }

    private Task PersistAsync(IndexFileModel model, CancellationToken cancellationToken)
    {
        IndexIdentity identity = GetIdentity();
        return IndexFileStore.PublishAsync(
            storage.GetDatabaseDirectory(identity.Database), model, cancellationToken);
    }

    private IndexIdentity GetIdentity()
    {
        return IndexIdentity.Decode(this.GetPrimaryKeyString());
    }

    private string GetManifestPath(IndexIdentity identity)
    {
        string databaseDirectory = storage.GetDatabaseDirectory(identity.Database);
        string path = IndexFileStore.GetManifestPath(
            databaseDirectory,
            identity.EventType,
            new EventKey(identity.Key.Name, identity.Key.Value));
        return Path.GetRelativePath(databaseDirectory, path);
    }

    private static bool Matches(SequencedEventMessage value, IndexIdentity identity)
    {
        return string.Equals(value.Type, identity.EventType, StringComparison.Ordinal) &&
               value.Keys.Contains(identity.Key);
    }
}