using System.Text.Json;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.Indexes;
using NativeDCB.Model;

using Orleans.Concurrency;

namespace NativeDCB.Actors.Grains;

[StatelessWorker]
// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class IndexOrchestratorGrain(ActorStoragePath storage, IGrainFactory grains)
    : Grain, IIndexOrchestratorGrain
{
    public async Task<IndexListResultMessage> ListIndexesAsync(GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        if (ValidateDatabase(database) is { } error)
        {
            return new IndexListResultMessage([], error);
        }

        long mainHead = await grains.GetGrain<IReadGrain>(database)
            .CaptureHeadAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        string directory = storage.GetDatabaseDirectory(database);
        HashSet<string> identities = new(StringComparer.Ordinal);
        foreach (string path in IndexFileStore.EnumerateManifestPaths(directory))
        {
            cancellationToken.CancellationToken.ThrowIfCancellationRequested();
            try
            {
                IndexManifestModel? manifest = await IndexFileStore.TryReadManifestAsync(
                        path, cancellationToken.CancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                if (manifest is null || string.IsNullOrEmpty(manifest.EventType) || manifest.Key is null ||
                    string.IsNullOrEmpty(manifest.Key.Name) || manifest.Key.Value is null)
                {
                    continue;
                }

                string expectedPath = IndexFileStore.GetManifestPath(directory, manifest.EventType, manifest.Key);
                if (!string.Equals(Path.GetFullPath(path), Path.GetFullPath(expectedPath),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                identities.Add(IndexIdentity.Encode(
                    database,
                    manifest.EventType,
                    new EventKeyMessage(manifest.Key.Name, manifest.Key.Value)));
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                                        UnauthorizedAccessException)
            {
                // A corrupt derived manifest without a trustworthy identity is ignored until rebuilt.
            }
        }

        Task<IndexStatusMessage>[] reads = identities.Order(StringComparer.Ordinal)
            .Select(identity => grains.GetGrain<IIndexGrain>(identity)
                .GetStatusAsync(mainHead, cancellationToken))
            .ToArray();
        IndexStatusMessage[] indexes = await Task.WhenAll(reads)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new IndexListResultMessage(indexes);
    }

    public async Task<AdministrationOperationResultMessage> RequestIndexRebuildAsync(
        string eventType,
        EventKeyMessage[] keys,
        GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        if (ValidateDatabase(database) is { } error)
        {
            return new AdministrationOperationResultMessage(error);
        }

        long mainHead = await grains.GetGrain<IReadGrain>(database)
            .CaptureHeadAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        Task[] rebuilds = keys.Distinct()
            .Select(key => grains.GetGrain<IIndexGrain>(IndexIdentity.Encode(database, eventType, key))
                .RebuildAsync(mainHead, cancellationToken))
            .ToArray();
        await Task.WhenAll(rebuilds)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new AdministrationOperationResultMessage();
    }

    public async Task<IndexQueryResultMessage> ReadAuthoritativeAsync(
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        ValidateReadArguments(afterEventIdExclusive, throughEventIdInclusive, maxCount);
        string database = this.GetPrimaryKeyString();
        WriterStateMessage state = await grains.GetGrain<IMainWriterGrain>(database)
            .GetStateAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        long observedHead = state.Head;
        long through = Math.Min(throughEventIdInclusive ?? observedHead, observedHead);
        if (!IsIndexSupported(query))
        {
            return await ReadAuthoritativeScanAsync(
                    database, query, observedHead, afterEventIdExclusive, through, maxCount, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        Dictionary<string, IndexSnapshotMessage> snapshots = await ReadSnapshotsAsync(
                database, query, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (snapshots.Values.Any(snapshot => !snapshot.Supported))
        {
            return await ReadAuthoritativeScanAsync(
                    database, query, observedHead, afterEventIdExclusive, through, maxCount, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        long prefixHead = snapshots.Values.Min(snapshot => Math.Min(Math.Max(0, snapshot.Head), through));
        IEnumerable<SequencedEventMessage> prefix =
            IndexQueryCombinationKernel.Combine(database, query, snapshots, prefixHead).Values;
        IEnumerable<SequencedEventMessage> tail = [];
        if (prefixHead < through)
        {
            EventListMessage range = await grains.GetGrain<IReadGrain>(database)
                .ReadRangeAsync(prefixHead + 1, through, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            tail = range.Events.Where(value => Matches(value, query));
        }

        return Result(
            observedHead,
            prefix.Concat(tail)
                .Where(value => value.EventId > afterEventIdExclusive && value.EventId <= through)
                .OrderBy(value => value.EventId)
                .Take(maxCount));
    }

    public async Task<IndexQueryResultMessage> ReadEventualAsync(
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        ValidateReadArguments(afterEventIdExclusive, throughEventIdInclusive, maxCount);
        string database = this.GetPrimaryKeyString();
        if (IsIndexSupported(query))
        {
            Dictionary<string, IndexSnapshotMessage> snapshots = await ReadSnapshotsAsync(
                    database, query, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (snapshots.Values.All(snapshot => snapshot.Supported))
            {
                long committedHead = await grains.GetGrain<IReadGrain>(database)
                    .CaptureHeadAsync(cancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
                long indexHead = Math.Min(
                    committedHead,
                    snapshots.Values.Min(snapshot => Math.Max(0, snapshot.Head)));
                long through = Math.Min(throughEventIdInclusive ?? indexHead, indexHead);
                return Result(
                    indexHead,
                    IndexQueryCombinationKernel.Combine(database, query, snapshots, through).Values
                        .Where(value => value.EventId > afterEventIdExclusive)
                        .OrderBy(value => value.EventId)
                        .Take(maxCount));
            }
        }

        EventReadSnapshotMessage snapshot = await grains.GetGrain<IReadGrain>(database)
            .ReadQuerySnapshotAsync(
                query,
                afterEventIdExclusive,
                throughEventIdInclusive,
                maxCount,
                cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new IndexQueryResultMessage(snapshot.ObservedHead, snapshot.Events);
    }

    private async Task<Dictionary<string, IndexSnapshotMessage>> ReadSnapshotsAsync(
        string database,
        EventQueryMessage query,
        GrainCancellationToken cancellationToken)
    {
        string[] identities = query.Items
            .SelectMany(item => item.EventTypes.SelectMany(eventType =>
                item.Keys.Select(key => IndexIdentity.Encode(database, eventType, key))))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Task<IndexSnapshotMessage>[] reads = identities.Select(identity =>
            grains.GetGrain<IIndexReplicaGrain>(identity).ReadSnapshotAsync(cancellationToken)).ToArray();
        IndexSnapshotMessage[] snapshots = await Task.WhenAll(reads)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return identities.Zip(snapshots).ToDictionary(pair => pair.First, pair => pair.Second,
            StringComparer.Ordinal);
    }

    private static bool IsIndexSupported(EventQueryMessage query)
    {
        return query.Items.Length > 0 &&
               query.Items.All(item => item.EventTypes.Length > 0 && item.Keys.Length > 0);
    }

    private async Task<IndexQueryResultMessage> ReadAuthoritativeScanAsync(
        string database,
        EventQueryMessage query,
        long observedHead,
        long afterEventIdExclusive,
        long throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        if (throughEventIdInclusive == 0)
        {
            return new IndexQueryResultMessage(observedHead, []);
        }

        EventListMessage events = await grains.GetGrain<IReadGrain>(database)
            .ReadByQueryAsync(query, throughEventIdInclusive, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return Result(
            observedHead,
            events.Events
                .Where(value => value.EventId > afterEventIdExclusive)
                .Take(maxCount));
    }

    private static bool Matches(SequencedEventMessage value, EventQueryMessage query)
    {
        return query.Items.Length == 0 || query.Items.Any(item =>
            (item.EventTypes.Length == 0 || item.EventTypes.Contains(value.Type, StringComparer.Ordinal)) &&
            item.Keys.All(value.Keys.Contains));
    }

    private static void ValidateReadArguments(
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount)
    {
        if (afterEventIdExclusive < 0 || throughEventIdInclusive < 0 ||
            (throughEventIdInclusive is not null && throughEventIdInclusive <= afterEventIdExclusive))
        {
            throw new ArgumentOutOfRangeException(nameof(afterEventIdExclusive));
        }

        if (maxCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxCount));
        }
    }

    private AdministrationErrorMessage? ValidateDatabase(string database)
    {
        try
        {
            _ = storage.GetDatabaseDirectory(database);
            return null;
        }
        catch (ArgumentException exception)
        {
            return new AdministrationErrorMessage(AdministrationErrorKind.InvalidArgument, exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return new AdministrationErrorMessage(AdministrationErrorKind.NotFound, exception.Message);
        }
    }

    private static IndexQueryResultMessage Result(
        long observedHead,
        IEnumerable<SequencedEventMessage> events)
    {
        return new IndexQueryResultMessage(
            observedHead,
            events.GroupBy(value => value.EventId)
                .Select(group => group.First())
                .OrderBy(value => value.EventId)
                .ToArray());
    }
}