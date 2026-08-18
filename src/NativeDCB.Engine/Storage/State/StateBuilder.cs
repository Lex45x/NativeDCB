using System.Security.Cryptography;
using System.Text.Json;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.State;

public sealed class StateBuilder
{
    public const string EventSchemaFingerprint = "native-dcb-event-v1";
    public const string KeyEncodingFingerprint = "event-key-name-value-v1";
    public const string StateSchemaFingerprint = "native-dcb-main-writer-state-v2";
    private readonly string _directory;

    public StateBuilder(DatabaseOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _directory = options.ValidateAndGetDirectory();
    }

    public async Task<StateFileModel> BuildAsync(
        int partitionNumber,
        CancellationToken cancellationToken = default)
    {
        if (partitionNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionNumber));
        }

        IReadOnlyList<(int Number, string Path)> allPartitions = JsonEventStore.DiscoverPartitions(_directory);
        if (partitionNumber >= allPartitions[^1].Number)
        {
            throw new InvalidOperationException("State files can only be built for closed partitions.");
        }

        (int Number, string Path)[] sources = allPartitions.Where(item => item.Number <= partitionNumber).ToArray();
        List<FileStream> sourceLocks = new(sources.Length);
        try
        {
            foreach ((_, string path) in sources)
            {
                sourceLocks.Add(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }

            DatabaseMetadata metadata =
                await ReadMetadataAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            RecoveredLog? checkpoint = await StateCheckpointRecovery.TryLoadLatestAsync(
                    _directory, metadata.StoreId, sources, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            int completedPartitions = checkpoint?.Partitions.Count ?? 0;
            RecoveredLog recovered = await LogRecovery.RecoverAsync(
                sources.Skip(completedPartitions).ToArray(),
                trimActivePartition: false,
                tolerateIncompleteActivePartition: false,
                metadata.StoreId,
                checkpoint,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            StateFileModel state = await CreateStateAsync(
                    metadata.StoreId, partitionNumber, recovered, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            string statePath = Path.Combine(_directory, $"state_{partitionNumber:000000}_v1.json");

            if (File.Exists(statePath))
            {
                StateFileModel? existing = await TryReadStateAsync(statePath, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (existing is not null && IsEquivalentBoundary(existing, state))
                {
                    return existing;
                }

                File.Delete(statePath);
            }

            await using FileStream output = new(
                statePath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            await JsonSerializer.SerializeAsync(output, state, StorageJson.Options, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            await output.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            output.Flush(flushToDisk: true);
            output.Position = 0;
            StateFileModel? validation = await JsonSerializer.DeserializeAsync<StateFileModel>(
                output,
                StorageJson.Options,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            if (validation is null || !IsEquivalentBoundary(validation, state))
            {
                throw new InvalidDataException($"Generated state file '{statePath}' failed validation.");
            }

            return state;
        }
        finally
        {
            foreach (FileStream sourceLock in sourceLocks)
            {
                await sourceLock.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            }
        }
    }

    private static async Task<StateFileModel> CreateStateAsync(
        Guid storeId,
        int partitionNumber,
        RecoveredLog recovered,
        CancellationToken cancellationToken)
    {
        CommandState[] commands = recovered.Commands
            .OrderBy(pair => pair.Value[index: 0].EventId)
            .Select(pair => new CommandState(
                pair.Key,
                pair.Value[index: 0].EventId,
                pair.Value[^1].EventId,
                pair.Value.Count))
            .ToArray();

        Dictionary<(string Type, string Name, string Value), long> latest = new();
        foreach (SequencedEvent @event in recovered.Events)
        {
            foreach (EventKey key in @event.Keys)
            {
                latest[(@event.Type, key.Name, key.Value)] = @event.EventId;
            }
        }

        LatestEventState[] latestEvents = latest
            .OrderBy(pair => pair.Key.Type, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Name, StringComparer.Ordinal)
            .ThenBy(pair => pair.Key.Value, StringComparer.Ordinal)
            .Select(pair => new LatestEventState(
                pair.Key.Type,
                new EventKey(pair.Key.Name, pair.Key.Value),
                pair.Value))
            .ToArray();

        List<PartitionCheckpoint> partitions = new(recovered.Partitions.Count);
        foreach (RecoveredPartition partition in recovered.Partitions)
        {
            await using FileStream stream = new(
                partition.Path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16_384,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            partitions.Add(new PartitionCheckpoint(
                partition.Number,
                partition.FirstEventId,
                partition.LastEventId,
                partition.EventCount,
                stream.Length,
                Convert.ToHexString(hash).ToLowerInvariant()));
        }

        return new StateFileModel(
            FormatVersion: 1,
            storeId,
            partitionNumber,
            recovered.Head,
            recovered.Events.Count,
            recovered.Events.ToArray(),
            recovered.Events.Select(@event => @event.EventId).ToArray(),
            commands,
            latestEvents,
            partitions,
            ComputeEventSnapshotFingerprint(recovered.Events),
            EventSchemaFingerprint,
            KeyEncodingFingerprint,
            StateSchemaFingerprint);
    }

    internal static string ComputeEventSnapshotFingerprint(IReadOnlyList<SequencedEvent> events)
    {
        return Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(events, StorageJson.Options)))
            .ToLowerInvariant();
    }

    private async Task<DatabaseMetadata> ReadMetadataAsync(CancellationToken cancellationToken)
    {
        string path = Path.Combine(_directory, "database_v1.json");
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<DatabaseMetadata>(
                   stream,
                   StorageJson.Options,
                   cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
               ?? throw new InvalidDataException($"Database metadata '{path}' is invalid.");
    }

    private static async Task<StateFileModel?> TryReadStateAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<StateFileModel>(
                stream,
                StorageJson.Options,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool IsEquivalentBoundary(StateFileModel candidate, StateFileModel expected)
    {
        return candidate.FormatVersion == 1 &&
               candidate.StoreId == expected.StoreId &&
               candidate.PartitionNumber == expected.PartitionNumber &&
               candidate.Head == expected.Head &&
               candidate.CommittedEventCount == expected.CommittedEventCount &&
               string.Equals(candidate.EventSchemaFingerprint, EventSchemaFingerprint, StringComparison.Ordinal) &&
               string.Equals(candidate.KeyEncodingFingerprint, KeyEncodingFingerprint, StringComparison.Ordinal) &&
               string.Equals(candidate.StateSchemaFingerprint, StateSchemaFingerprint, StringComparison.Ordinal);
    }
}