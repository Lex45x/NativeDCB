using System.Text;
using System.Text.Json;

using NativeDCB.Model;

// Orleans discovers grain implementations at runtime.
// ReSharper disable UnusedType.Global

namespace NativeDCB.Engine.Actors;

public sealed class IndexCoordinatorGrain(IDatabaseStoreProvider stores, IGrainFactory grains)
    : Grain, IIndexCoordinatorGrain
{
    private readonly HashSet<string> _identities = new(StringComparer.Ordinal);
    private bool _discovered;

    public async Task ObserveAsync(EventListMessage events, long head)
    {
        await EnsureDiscoveredAsync()
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        string database = this.GetPrimaryKeyString();
        foreach (SequencedEventMessage @event in events.Events)
        {
            foreach (EventKeyMessage key in @event.Keys)
            {
                _identities.Add(IndexIdentity.Encode(database, @event.Type, key));
            }
        }

        foreach (string identity in _identities)
        {
            await grains.GetGrain<IIndexGrain>(identity).ObserveAsync(events, head)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public async Task<IndexListMessage> ListAsync(long mainHead, GrainCancellationToken cancellationToken)
    {
        await EnsureDiscoveredAsync()
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        List<IndexStatusMessage> statuses = new(_identities.Count);
        foreach (string identity in _identities.Order(StringComparer.Ordinal))
        {
            cancellationToken.CancellationToken.ThrowIfCancellationRequested();
            IndexStatusMessage status = await grains.GetGrain<IIndexGrain>(identity)
                .GetStatusAsync(mainHead, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            statuses.Add(status);
        }

        return new IndexListMessage(statuses.ToArray());
    }

    public async Task<IndexReadResultMessage> ReadAsync(
        EventQueryMessage query,
        GrainCancellationToken cancellationToken)
    {
        await EnsureDiscoveredAsync()
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (query.Items.Length == 0 || query.Items.Any(item => item.EventTypes.Length == 0 || item.Keys.Length == 0))
        {
            return new IndexReadResultMessage(Supported: false, IndexHead: 0, []);
        }

        string database = this.GetPrimaryKeyString();
        Dictionary<long, SequencedEventMessage> results = new();
        long indexHead = long.MaxValue;
        foreach (QueryItemMessage item in query.Items)
        {
            foreach (string eventType in item.EventTypes)
            {
                Dictionary<long, SequencedEventMessage>? intersection = null;
                foreach (EventKeyMessage key in item.Keys)
                {
                    cancellationToken.CancellationToken.ThrowIfCancellationRequested();
                    string identity = IndexIdentity.Encode(database, eventType, key);
                    if (!_identities.Contains(identity))
                    {
                        indexHead = 0;
                        intersection = [];
                        break;
                    }

                    IIndexGrain index = grains.GetGrain<IIndexGrain>(identity);
                    IndexStatusMessage status = await index
                        .GetStatusAsync(mainHead: 0, cancellationToken)
                        .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                    indexHead = Math.Min(indexHead, status.IndexHead);
                    EventListMessage indexed = await index
                        .ReadAsync(cancellationToken)
                        .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                    Dictionary<long, SequencedEventMessage> current = indexed.Events
                        .ToDictionary(value => value.EventId);
                    if (intersection is null)
                    {
                        intersection = current;
                    }
                    else
                    {
                        foreach (long eventId in intersection.Keys.Except(current.Keys).ToArray())
                        {
                            intersection.Remove(eventId);
                        }
                    }
                }

                if (intersection is not null)
                {
                    foreach ((long eventId, SequencedEventMessage value) in intersection)
                    {
                        results[eventId] = value;
                    }
                }
            }
        }

        return new IndexReadResultMessage(
            Supported: true,
            indexHead == long.MaxValue ? 0 : indexHead,
            results.Values.OrderBy(value => value.EventId).ToArray());
    }

    public async Task RebuildAsync(string eventType, EventKeyMessage[] keys, long mainHead)
    {
        string database = this.GetPrimaryKeyString();
        foreach (EventKeyMessage key in keys)
        {
            string identity = IndexIdentity.Encode(database, eventType, key);
            _identities.Add(identity);
            await grains.GetGrain<IIndexGrain>(identity).RebuildAsync(mainHead)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    private async Task EnsureDiscoveredAsync()
    {
        if (_discovered)
        {
            return;
        }

        string database = this.GetPrimaryKeyString();
        string directory = stores.GetDirectory(database);
        foreach (string path in Directory.EnumerateFiles(directory, "index_*_v1.json"))
        {
            try
            {
                IndexFileModel? model = await IndexFileStore.TryReadAsync(path, CancellationToken.None)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                if (model is not null)
                {
                    _identities.Add(IndexIdentity.Encode(
                        database,
                        model.EventType,
                        new EventKeyMessage(model.Key.Name, model.Key.Value)));
                }
            }
            catch (JsonException)
            {
                // Corrupt derived files are ignored until an explicit rebuild or matching commit recreates them.
            }
        }

        _discovered = true;
    }
}

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

internal sealed record IndexIdentity(string Database, string EventType, EventKeyMessage Key)
{
    public static string Encode(string database, string eventType, EventKeyMessage key)
    {
        return string.Join(separator: '.',
            EncodePart(database), EncodePart(eventType), EncodePart(key.Name), EncodePart(key.Value));
    }

    public static IndexIdentity Decode(string value)
    {
        string[] parts = value.Split(separator: '.');
        if (parts.Length != 4)
        {
            throw new InvalidOperationException("The index grain identity is invalid.");
        }

        return new IndexIdentity(
            DecodePart(parts[0]),
            DecodePart(parts[1]),
            new EventKeyMessage(DecodePart(parts[2]), DecodePart(parts[3])));
    }

    private static string EncodePart(string value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value))
            .TrimEnd(trimChar: '=').Replace(oldChar: '+', newChar: '-').Replace(oldChar: '/', newChar: '_');
    }

    private static string DecodePart(string value)
    {
        string encoded = value.Replace(oldChar: '-', newChar: '+').Replace(oldChar: '_', newChar: '/');
        encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, paddingChar: '=');
        return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
    }
}