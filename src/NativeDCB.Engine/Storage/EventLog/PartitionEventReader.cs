using System.Text.Json;

using NativeDCB.Model;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.Engine.Storage.EventLog;

public static class PartitionEventReader
{
    public static async Task<long> GetHeadAsync(
        string directory,
        CancellationToken cancellationToken = default)
    {
        return (await RecoverAsync(directory, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)).Head;
    }

    public static async Task<IReadOnlyList<PartitionStatus>> ListPartitionStatusAsync(
        string directory,
        long throughEventIdInclusive,
        CancellationToken cancellationToken = default)
    {
        if (throughEventIdInclusive < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(throughEventIdInclusive));
        }

        RecoveredLog recovered = await RecoverAsync(directory, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return recovered.Partitions.Select((partition, index) =>
        {
            SequencedEvent[] visible = partition.FirstEventId is null
                ? []
                : recovered.Events.Where(value =>
                    value.EventId >= partition.FirstEventId &&
                    value.EventId <= partition.LastEventId &&
                    value.EventId <= throughEventIdInclusive).ToArray();
            return new PartitionStatus(
                partition.Number,
                partition.Path,
                index == recovered.Partitions.Count - 1,
                visible.Length == 0 ? null : visible[0].EventId,
                visible.Length == 0 ? null : visible[^1].EventId,
                visible.Length,
                new FileInfo(partition.Path).Length);
        }).ToArray();
    }

    public static async Task<IReadOnlyList<SequencedEvent>> ReadRangeAsync(
        string directory,
        long fromEventIdInclusive,
        long toEventIdInclusive,
        CancellationToken cancellationToken = default)
    {
        if (fromEventIdInclusive < 1 || toEventIdInclusive < fromEventIdInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(fromEventIdInclusive));
        }

        RecoveredLog recovered = await RecoverAsync(directory, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return recovered.Events.Where(value =>
            value.EventId >= fromEventIdInclusive && value.EventId <= toEventIdInclusive).ToArray();
    }

    public static async Task<IReadOnlyList<SequencedEvent>> ReadByQueryAsync(
        string directory,
        EventQuery query,
        long throughEventIdInclusive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (throughEventIdInclusive < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(throughEventIdInclusive));
        }

        RecoveredLog recovered = await RecoverAsync(directory, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return recovered.Events.Where(value =>
            value.EventId <= throughEventIdInclusive && query.Matches(value)).ToArray();
    }

    public static async Task<IReadOnlyList<SequencedEvent>> ReadByCommandIdAsync(
        string directory,
        Guid commandId,
        long throughEventIdInclusive,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command ID is required.", nameof(commandId));
        }

        if (throughEventIdInclusive < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(throughEventIdInclusive));
        }

        RecoveredLog recovered = await RecoverAsync(directory, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return recovered.Commands.TryGetValue(commandId, out List<SequencedEvent>? events)
            ? events.Where(value => value.EventId <= throughEventIdInclusive).ToArray()
            : [];
    }

    private static async Task<RecoveredLog> RecoverAsync(string directory, CancellationToken cancellationToken)
    {
        string path = Path.GetFullPath(directory);
        DatabaseMetadata metadata = await ReadMetadataAsync(path, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return await LogRecovery.RecoverAsync(
            JsonEventStore.DiscoverPartitions(path),
            trimActivePartition: false,
            tolerateIncompleteActivePartition: true,
            metadata.StoreId,
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task<DatabaseMetadata> ReadMetadataAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, "database_v1.json");
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<DatabaseMetadata>(
                   stream, StorageJson.Options, cancellationToken).ConfigureAwait(continueOnCapturedContext: false)
               ?? throw new InvalidDataException($"Database metadata '{path}' is invalid.");
    }
}