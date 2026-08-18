using System.Text.Json;

using NativeDCB.Model;
using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.EventLog;

internal static class LogRecovery
{
    public static async Task<RecoveredLog> RecoverAsync(
        IReadOnlyList<(int Number, string Path)> partitions,
        bool trimActivePartition,
        bool tolerateIncompleteActivePartition,
        Guid expectedStoreId,
        CancellationToken cancellationToken)
    {
        return await RecoverAsync(
            partitions,
            trimActivePartition,
            tolerateIncompleteActivePartition,
            expectedStoreId,
            seed: null,
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    public static async Task<RecoveredLog> RecoverAsync(
        IReadOnlyList<(int Number, string Path)> partitions,
        bool trimActivePartition,
        bool tolerateIncompleteActivePartition,
        Guid expectedStoreId,
        RecoveredLog? seed,
        CancellationToken cancellationToken)
    {
        RecoveredLog recovered = seed ?? new RecoveredLog();

        for (int index = 0; index < partitions.Count; index++)
        {
            (int number, string path) = partitions[index];
            bool isActive = index == partitions.Count - 1;
            RecoveredPartition partition = await ReplayPartitionAsync(
                number,
                path,
                isActive && (trimActivePartition || tolerateIncompleteActivePartition),
                isActive && trimActivePartition,
                expectedStoreId,
                recovered,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            recovered.Partitions.Add(partition);
        }

        return recovered;
    }

    private static async Task<RecoveredPartition> ReplayPartitionAsync(
        int number,
        string path,
        bool mayHaveIncompleteSuffix,
        bool trimIncompleteSuffix,
        Guid expectedStoreId,
        RecoveredLog recovered,
        CancellationToken cancellationToken)
    {
        List<(LogRecord Record, SequencedEvent Event)> pending = new();
        int parsedRecordCount = 0;
        int committedRecordCount = 0;
        int partitionEventCount = 0;
        long? firstEventId = null;
        long? lastEventId = null;
        bool malformedSuffix = false;

        await using (FileStream stream = new(
                         path,
                         FileMode.Open,
                         FileAccess.Read,
                         mayHaveIncompleteSuffix && !trimIncompleteSuffix ? FileShare.ReadWrite : FileShare.Read,
                         bufferSize: 16_384,
                         FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            try
            {
                await foreach (LogRecord? record in JsonSerializer.DeserializeAsyncEnumerable<LogRecord>(
                                   stream,
                                   topLevelValues: true,
                                   StorageJson.Options,
                                   cancellationToken).ConfigureAwait(continueOnCapturedContext: false))
                {
                    parsedRecordCount++;
                    if (record is null)
                    {
                        throw Corrupt(path, "A null storage record was found.");
                    }

                    switch (record.Kind)
                    {
                        case "partition" when parsedRecordCount == 1:
                            ValidatePartitionHeader(record, number, expectedStoreId, path);
                            committedRecordCount = 1;
                            break;

                        case "event":
                            if (parsedRecordCount == 1)
                            {
                                throw Corrupt(path, "The partition header is missing.");
                            }

                            SequencedEvent @event = ValidateEvent(record, pending, recovered.Head, path);
                            pending.Add((record, @event));
                            break;

                        case "commit":
                            ValidateCommit(record, pending, recovered.Head, path);
                            Guid commandId = pending[index: 0].Event.CommandId;
                            if (recovered.Commands.ContainsKey(commandId))
                            {
                                throw Corrupt(path, $"Command ID '{commandId}' identifies more than one batch.");
                            }

                            List<SequencedEvent> commandEvents = new(pending.Count);
                            recovered.Commands.Add(commandId, commandEvents);
                            foreach ((_, SequencedEvent committedEvent) in pending)
                            {
                                recovered.Events.Add(committedEvent);
                                commandEvents.Add(committedEvent);
                                firstEventId ??= committedEvent.EventId;
                                lastEventId = committedEvent.EventId;
                                partitionEventCount++;
                            }

                            pending.Clear();
                            committedRecordCount = parsedRecordCount;
                            break;

                        default:
                            throw Corrupt(path, $"Unknown storage record kind '{record.Kind ?? "<null>"}'.");
                    }
                }
            }
            catch (JsonException exception) when (mayHaveIncompleteSuffix)
            {
                ObjectScan scan = await ScanObjectsAsync(
                    path,
                    !trimIncompleteSuffix,
                    cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                if (!scan.HasIncompleteObject || scan.CompleteObjectEnds.Count < parsedRecordCount)
                {
                    throw Corrupt(path, "Malformed JSON was found in the committed record prefix.", exception);
                }

                malformedSuffix = true;
            }
            catch (JsonException exception)
            {
                throw Corrupt(path, "Malformed JSON was found in a closed partition.", exception);
            }
        }

        if ((pending.Count > 0 || malformedSuffix) && !mayHaveIncompleteSuffix)
        {
            throw Corrupt(path, "A closed partition ends with an uncommitted batch.");
        }


        if (parsedRecordCount == 0)
        {
            throw Corrupt(path, "The partition header is missing.");
        }

        if (trimIncompleteSuffix && (pending.Count > 0 || malformedSuffix))
        {
            ObjectScan scan = await ScanObjectsAsync(
                path,
                allowConcurrentWriter: false,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            long committedLength = committedRecordCount == 0 ? 0 : scan.CompleteObjectEnds[committedRecordCount - 1];
            await using FileStream writable = new(
                path,
                FileMode.Open,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 1,
                FileOptions.Asynchronous);
            writable.SetLength(committedLength);
            writable.Flush(flushToDisk: true);
        }

        return new RecoveredPartition(number, path, firstEventId, lastEventId, partitionEventCount);
    }

    private static SequencedEvent ValidateEvent(
        LogRecord record,
        IReadOnlyList<(LogRecord Record, SequencedEvent Event)> pending,
        long committedHead,
        string path)
    {
        if (record.EventId is not > 0 || record.BatchId is null || record.BatchId == Guid.Empty ||
            record.BatchIndex is null || record.BatchCount is not > 0 || string.IsNullOrWhiteSpace(record.Type) ||
            record.SchemaVersion is not > 0 || record.Keys is null || record.Data is null ||
            record.Data.Value.ValueKind == JsonValueKind.Undefined || record.TimestampUtc is null ||
            record.CommandId is null || record.CommandId == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.CommandType))
        {
            throw Corrupt(path, "An event record is missing required metadata.");
        }

        if (record.BatchIndex != pending.Count || record.BatchCount <= pending.Count)
        {
            throw Corrupt(path, "Event batch indexes or counts are inconsistent.");
        }

        bool eventIdIsValid = pending.Count == 0
            ? record.EventId > committedHead
            : record.EventId == pending[^1].Event.EventId + 1;
        if (!eventIdIsValid)
        {
            throw Corrupt(path, "Event IDs are not consecutive and globally ordered.");
        }

        if (pending.Count > 0)
        {
            LogRecord first = pending[index: 0].Record;
            if (record.BatchId != first.BatchId || record.BatchCount != first.BatchCount ||
                record.CommandId != first.CommandId ||
                !string.Equals(record.CommandType, first.CommandType, StringComparison.Ordinal))
            {
                throw Corrupt(path, "Event records from different batches are interleaved.");
            }
        }

        ValidateKeys(record.Keys, path);
        return new SequencedEvent(
            record.EventId.Value,
            record.Type,
            record.Data.Value.Clone(),
            record.Keys.ToArray(),
            record.SchemaVersion.Value,
            record.TimestampUtc.Value,
            record.CommandId.Value,
            record.CommandType);
    }

    private static void ValidatePartitionHeader(
        LogRecord record,
        int partitionNumber,
        Guid expectedStoreId,
        string path)
    {
        if (record.FormatVersion != 1 ||
            record.StoreId != expectedStoreId ||
            record.PartitionNumber != partitionNumber)
        {
            throw Corrupt(path, "The partition identity does not match its database, number, or format.");
        }
    }

    private static void ValidateCommit(
        LogRecord commit,
        IReadOnlyList<(LogRecord Record, SequencedEvent Event)> pending,
        long committedHead,
        string path)
    {
        if (pending.Count == 0 || commit.BatchId is null || commit.BatchId == Guid.Empty ||
            commit.BatchCount is not > 0 || commit.FirstEventId is not > 0 || commit.LastEventId is not > 0)
        {
            throw Corrupt(path, "A commit record has no complete preceding batch.");
        }

        LogRecord first = pending[index: 0].Record;
        if (commit.BatchId != first.BatchId || commit.BatchCount != pending.Count ||
            first.BatchCount != pending.Count || commit.FirstEventId != pending[index: 0].Event.EventId ||
            commit.LastEventId != pending[^1].Event.EventId || commit.FirstEventId <= committedHead)
        {
            throw Corrupt(path, "A commit marker does not match its event batch.");
        }

        Guid commandId = pending[index: 0].Event.CommandId;
        if (pending.Any(item => item.Event.CommandId != commandId))
        {
            throw Corrupt(path, "A committed batch contains multiple command IDs.");
        }
    }

    private static void ValidateKeys(IReadOnlyList<EventKey> keys, string path)
    {
        if (keys.Count == 0)
        {
            throw Corrupt(path, "An event has no consistency keys.");
        }

        HashSet<EventKey> unique = new();
        foreach (EventKey key in keys)
        {
            if (string.IsNullOrWhiteSpace(key.Name) || string.IsNullOrWhiteSpace(key.Value) || !unique.Add(key))
            {
                throw Corrupt(path, "An event contains an invalid or duplicate key.");
            }
        }
    }

    private static async Task<ObjectScan> ScanObjectsAsync(
        string path,
        bool allowConcurrentWriter,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            allowConcurrentWriter ? FileShare.ReadWrite : FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        byte[] bytes = GC.AllocateUninitializedArray<byte>(checked((int)stream.Length));
        await stream.ReadExactlyAsync(bytes, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        List<long> ends = new();
        bool inObject = false;
        bool inString = false;
        bool escaped = false;
        int depth = 0;

        for (int index = 0; index < bytes.Length; index++)
        {
            byte value = bytes[index];
            if (!inObject)
            {
                if (IsWhitespace(value))
                {
                    if (ends.Count > 0)
                    {
                        ends[^1] = index + 1L;
                    }

                    continue;
                }

                if (value != (byte)'{')
                {
                    return new ObjectScan(ends, HasIncompleteObject: false);
                }

                inObject = true;
                depth = 1;
                continue;
            }

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (value == (byte)'\\')
                {
                    escaped = true;
                }
                else if (value == (byte)'"')
                {
                    inString = false;
                }

                continue;
            }

            if (value == (byte)'"')
            {
                inString = true;
            }
            else if (value is (byte)'{' or (byte)'[')
            {
                depth++;
            }
            else if (value is (byte)'}' or (byte)']')
            {
                depth--;
                if (depth == 0)
                {
                    ends.Add(index + 1L);
                    inObject = false;
                }
                else if (depth < 0)
                {
                    return new ObjectScan(ends, HasIncompleteObject: false);
                }
            }
        }

        return new ObjectScan(ends, inObject);
    }

    private static bool IsWhitespace(byte value)
    {
        return value is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n';
    }

    private static InvalidDataException Corrupt(string path, string message, Exception? inner = null)
    {
        return new InvalidDataException($"Partition '{path}' is corrupt: {message}", inner);
    }

    private sealed record ObjectScan(IReadOnlyList<long> CompleteObjectEnds, bool HasIncompleteObject);
}