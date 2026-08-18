using System.Text.Json;

using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Actors.Messages;
using NativeDCB.Engine.Storage.Indexes;

// Orleans discovers grain implementations at runtime.
// ReSharper disable UnusedType.Global

namespace NativeDCB.Engine.Actors.Grains;

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