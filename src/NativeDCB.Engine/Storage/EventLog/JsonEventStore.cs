using System.Text.Json;
using System.Threading.Channels;

using NativeDCB.Engine.Storage.State;
using NativeDCB.Model;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Model.Queries;

namespace NativeDCB.Engine.Storage.EventLog;

public sealed class JsonEventStore : IAsyncDisposable
{
    private const string MetadataFileName = "database_v1.json";
    private const string LockFileName = "store.lock";
    private readonly Dictionary<Guid, List<SequencedEvent>> _commands;

    private readonly Channel<SequencedEvent> _committedEvents = Channel.CreateUnbounded<SequencedEvent>(
        new UnboundedChannelOptions { SingleWriter = true, AllowSynchronousContinuations = false });

    private readonly List<SequencedEvent> _events;
    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly FileStream _lockHandle;
    private readonly DatabaseOptions _options;
    private readonly List<RecoveredPartition> _partitions;
    private FileStream _activeStream;
    private bool _disposed;
    private long _head;
    private Exception? _lastFault;

    private JsonEventStore(
        DatabaseOptions options,
        string directory,
        Guid storeId,
        FileStream lockHandle,
        FileStream activeStream,
        RecoveredLog recovered)
    {
        _options = options;
        DirectoryPath = directory;
        StoreId = storeId;
        _lockHandle = lockHandle;
        _activeStream = activeStream;
        _events = recovered.Events;
        _commands = recovered.Commands;
        _partitions = recovered.Partitions;
        _head = recovered.Head;
    }

    public Guid StoreId { get; }

    // Exposed as part of the public store API.
    // ReSharper disable once MemberCanBePrivate.Global
    public string DirectoryPath { get; }

    public long Head => Interlocked.Read(ref _head);

    public int ActivePartition => _partitions[^1].Number;

    public bool IsFaulted { get; private set; }

    public string? LastFault => _lastFault?.Message;

    public ChannelReader<SequencedEvent> CommittedEvents => _committedEvents.Reader;

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _committedEvents.Writer.TryComplete();
            await _activeStream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            await _lockHandle.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
        }
        finally
        {
            _gate.Release();
        }
    }

    public event Action<SequencedEvent>? EventCommitted;

    public static async Task<JsonEventStore> CreateAsync(
        DatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string directory = options.ValidateAndGetDirectory();
        Directory.CreateDirectory(directory);

        if (Directory.EnumerateFileSystemEntries(directory).Any())
        {
            throw new InvalidOperationException($"Database directory '{directory}' is not empty.");
        }

        FileStream? lockHandle = null;
        FileStream? activeStream = null;
        try
        {
            lockHandle = AcquireLock(directory);
            DatabaseMetadata metadata = new(FormatVersion: 1, Guid.NewGuid(), DateTimeOffset.UtcNow);
            await WriteDurableJsonAsync(Path.Combine(directory, MetadataFileName), metadata, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);

            string partitionPath = GetPartitionPath(directory, number: 1);
            await WritePartitionHeaderAsync(partitionPath, metadata.StoreId, partitionNumber: 1, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            activeStream = OpenAppendStream(partitionPath);
            RecoveredLog recovered = new();
            recovered.Partitions.Add(new RecoveredPartition(Number: 1, partitionPath, FirstEventId: null,
                LastEventId: null, EventCount: 0));
            return new JsonEventStore(options, directory, metadata.StoreId, lockHandle, activeStream, recovered);
        }
        catch
        {
            if (activeStream is not null)
            {
                await activeStream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            }

            if (lockHandle is not null)
            {
                await lockHandle.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            }

            throw;
        }
    }

    public static async Task<JsonEventStore> OpenAsync(
        DatabaseOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string directory = options.ValidateAndGetDirectory();
        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException($"Database directory '{directory}' does not exist.");
        }

        FileStream? lockHandle = null;
        FileStream? activeStream = null;
        try
        {
            lockHandle = AcquireLock(directory);
            DatabaseMetadata metadata = await ReadMetadataAsync(directory, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            IReadOnlyList<(int Number, string Path)> partitionFiles = DiscoverPartitions(directory);
            RecoveredLog? checkpoint = await StateCheckpointRecovery.TryLoadLatestAsync(
                    directory, metadata.StoreId, partitionFiles, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            int completedPartitions = checkpoint?.Partitions.Count ?? 0;
            RecoveredLog recovered = await LogRecovery.RecoverAsync(
                partitionFiles.Skip(completedPartitions).ToArray(),
                trimActivePartition: true,
                tolerateIncompleteActivePartition: false,
                metadata.StoreId,
                checkpoint,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            activeStream = OpenAppendStream(partitionFiles[^1].Path);
            return new JsonEventStore(options, directory, metadata.StoreId, lockHandle, activeStream, recovered);
        }
        catch
        {
            if (activeStream is not null)
            {
                await activeStream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            }

            if (lockHandle is not null)
            {
                await lockHandle.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);
            }

            throw;
        }
    }

    public async Task<AppendResult> AppendAsync(
        EventBatch batch,
        AppendCondition condition,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(batch);
        ArgumentNullException.ThrowIfNull(condition);
        ArgumentNullException.ThrowIfNull(condition.Query);
        ValidateBatch(batch);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            ThrowIfUnavailable();

            if (_commands.TryGetValue(batch.CommandId, out List<SequencedEvent>? existing))
            {
                return new AppendResult(AppendOutcome.AlreadyCommitted, batch.CommandId, existing.ToArray(), _head);
            }

            if (condition.AfterEventId < 0 || condition.AfterEventId > _head)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(condition),
                    "The observed event ID must be between zero and the current head.");
            }

            if (_events.Any(@event => @event.EventId > condition.AfterEventId && condition.Query.Matches(@event)))
            {
                return new AppendResult(AppendOutcome.Conflict, batch.CommandId, [], _head);
            }

            if (_partitions[^1].EventCount > 0 &&
                _partitions[^1].EventCount + batch.Events.Count > _options.MaxEventCountPerPartition)
            {
                try
                {
                    await RollPartitionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    MarkFault(exception);
                    throw new EventStoreUnavailableException("The event store could not open the next partition.",
                        exception);
                }
            }

            Guid batchId = Guid.NewGuid();
            DateTimeOffset timestamp = DateTimeOffset.UtcNow;
            long firstEventId = _head + 1;
            List<SequencedEvent> committed = new(batch.Events.Count);
            using MemoryStream buffer = new();

            for (int index = 0; index < batch.Events.Count; index++)
            {
                CandidateEvent candidate = batch.Events[index];
                long eventId = firstEventId + index;
                SequencedEvent sequenced = new(
                    eventId,
                    candidate.Type,
                    candidate.Data.Clone(),
                    candidate.Keys.ToArray(),
                    candidate.SchemaVersion,
                    timestamp,
                    batch.CommandId,
                    batch.CommandType);
                committed.Add(sequenced);

                LogRecord record = new()
                {
                    Kind = "event",
                    EventId = eventId,
                    BatchId = batchId,
                    BatchIndex = index,
                    BatchCount = batch.Events.Count,
                    Type = candidate.Type,
                    SchemaVersion = candidate.SchemaVersion,
                    Keys = candidate.Keys,
                    Data = candidate.Data,
                    TimestampUtc = timestamp,
                    CommandId = batch.CommandId,
                    CommandType = batch.CommandType
                };
                await WriteRecordAsync(buffer, record, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
            }

            LogRecord commit = new()
            {
                Kind = "commit",
                BatchId = batchId,
                BatchCount = batch.Events.Count,
                FirstEventId = firstEventId,
                LastEventId = firstEventId + batch.Events.Count - 1
            };
            await WriteRecordAsync(buffer, commit, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);

            try
            {
                buffer.Position = 0;
                await buffer.CopyToAsync(_activeStream, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                await _activeStream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                _activeStream.Flush(flushToDisk: true);
            }
            catch (Exception exception)
            {
                EventStoreUnavailableException unavailable = new(
                    "The append durability outcome is uncertain and the event store requires recovery.", exception);
                MarkFault(unavailable);
                throw unavailable;
            }

            _events.AddRange(committed);
            _commands.Add(batch.CommandId, committed);
            Interlocked.Exchange(ref _head, committed[^1].EventId);
            RecoveredPartition active = _partitions[^1];
            _partitions[^1] = active with
            {
                FirstEventId = active.FirstEventId ?? committed[index: 0].EventId,
                LastEventId = committed[^1].EventId,
                EventCount = active.EventCount + committed.Count
            };

            foreach (SequencedEvent @event in committed)
            {
                _committedEvents.Writer.TryWrite(@event);
                PublishEvent(@event);
            }

            if (_partitions[^1].EventCount >= _options.MaxEventCountPerPartition)
            {
                try
                {
                    await RollPartitionAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    // The batch is already durable and promoted. Fault future writes without changing its outcome.
                    MarkFault(exception);
                }
            }

            return new AppendResult(AppendOutcome.Committed, batch.CommandId, committed.ToArray(), _head);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<SequencedEvent>> ReadRangeAsync(
        long fromEventIdInclusive,
        long toEventIdInclusive = long.MaxValue,
        CancellationToken cancellationToken = default)
    {
        if (fromEventIdInclusive < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(fromEventIdInclusive));
        }

        if (toEventIdInclusive < fromEventIdInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(toEventIdInclusive));
        }

        return await ReadCoreAsync(
            @event => @event.EventId >= fromEventIdInclusive && @event.EventId <= toEventIdInclusive,
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    public Task<IReadOnlyList<SequencedEvent>> ReadByQueryAsync(
        EventQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        return ReadCoreAsync(query.Matches, cancellationToken);
    }

    public async Task<IReadOnlyList<SequencedEvent>> ReadByCommandIdAsync(
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        if (commandId == Guid.Empty)
        {
            throw new ArgumentException("A command ID is required.", nameof(commandId));
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            ThrowIfDisposed();
            return _commands.TryGetValue(commandId, out List<SequencedEvent>? events)
                ? events.ToArray()
                : [];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<PartitionStatus>> ListPartitionStatusAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            ThrowIfDisposed();
            return _partitions.Select((partition, index) => new PartitionStatus(
                partition.Number,
                partition.Path,
                index == _partitions.Count - 1,
                partition.FirstEventId,
                partition.LastEventId,
                partition.EventCount,
                new FileInfo(partition.Path).Length)).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<IReadOnlyList<SequencedEvent>> ReadCoreAsync(
        Func<SequencedEvent, bool> predicate,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            ThrowIfDisposed();
            return _events.Where(predicate).ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task RollPartitionAsync(CancellationToken cancellationToken)
    {
        await _activeStream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        _activeStream.Flush(flushToDisk: true);
        await _activeStream.DisposeAsync().ConfigureAwait(continueOnCapturedContext: false);

        int number = _partitions[^1].Number + 1;
        string path = GetPartitionPath(DirectoryPath, number);
        await WritePartitionHeaderAsync(path, StoreId, number, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        _activeStream = OpenAppendStream(path);
        _partitions.Add(new RecoveredPartition(number, path, FirstEventId: null, LastEventId: null, EventCount: 0));
    }

    private void PublishEvent(SequencedEvent @event)
    {
        Action<SequencedEvent>? handlers = EventCommitted;
        if (handlers is null)
        {
            return;
        }

        foreach (Action<SequencedEvent> handler in handlers.GetInvocationList().Cast<Action<SequencedEvent>>())
        {
            try
            {
                handler(@event);
            }
            catch
            {
                // A notification observer cannot change an already durable append outcome.
            }
        }
    }

    private void ThrowIfUnavailable()
    {
        ThrowIfDisposed();
        if (IsFaulted)
        {
            throw new EventStoreUnavailableException(
                $"The event store is faulted after a persistence failure: {_lastFault?.Message ?? "unknown failure"}");
        }
    }

    private void MarkFault(Exception exception)
    {
        _lastFault = exception;
        IsFaulted = true;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private static void ValidateBatch(EventBatch batch)
    {
        if (batch.CommandId == Guid.Empty)
        {
            throw new ArgumentException("A command ID is required.", nameof(batch));
        }

        if (string.IsNullOrWhiteSpace(batch.CommandType))
        {
            throw new ArgumentException("A command type is required.", nameof(batch));
        }

        if (batch.Events is null || batch.Events.Count == 0)
        {
            throw new ArgumentException("An append batch must contain at least one event.", nameof(batch));
        }

        foreach (CandidateEvent @event in batch.Events)
        {
            if (string.IsNullOrWhiteSpace(@event.Type) || @event.SchemaVersion == 0 ||
                @event.Data.ValueKind == JsonValueKind.Undefined || @event.Keys is null or { Count: 0 })
            {
                throw new ArgumentException(
                    "A candidate event must have valid required data and at least one consistency key.",
                    nameof(batch));
            }

            HashSet<EventKey> keys = new();
            if (@event.Keys.Any(key => string.IsNullOrWhiteSpace(key.Name) ||
                                       string.IsNullOrWhiteSpace(key.Value) || !keys.Add(key)))
            {
                throw new ArgumentException("Event keys must be non-empty and unique.", nameof(batch));
            }
        }
    }

    private static FileStream AcquireLock(string directory)
    {
        try
        {
            return new FileStream(
                Path.Combine(directory, LockFileName),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);
        }
        catch (IOException exception)
        {
            throw new IOException($"Database directory '{directory}' is already open for writing.", exception);
        }
    }

    private static FileStream OpenAppendStream(string path)
    {
        return new FileStream(
            path,
            FileMode.OpenOrCreate,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 16_384,
            FileOptions.Asynchronous | FileOptions.WriteThrough) { Position = new FileInfo(path).Length };
    }

    private static async Task WriteRecordAsync(Stream stream, LogRecord record, CancellationToken cancellationToken)
    {
        await JsonSerializer.SerializeAsync(stream, record, StorageJson.Options, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
    }

    private static async Task WritePartitionHeaderAsync(
        string path,
        Guid storeId,
        int partitionNumber,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await WriteRecordAsync(stream,
            new LogRecord
            {
                Kind = "partition", FormatVersion = 1, StoreId = storeId, PartitionNumber = partitionNumber
            }, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task WriteDurableJsonAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using FileStream stream = new(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(stream, value, StorageJson.Options, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task<DatabaseMetadata> ReadMetadataAsync(
        string directory,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(directory, MetadataFileName);
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        DatabaseMetadata? metadata = await JsonSerializer.DeserializeAsync<DatabaseMetadata>(
            stream,
            StorageJson.Options,
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (metadata is null || metadata.FormatVersion != 1 || metadata.StoreId == Guid.Empty)
        {
            throw new InvalidDataException($"Database metadata '{path}' is invalid.");
        }

        return metadata;
    }

    internal static IReadOnlyList<(int Number, string Path)> DiscoverPartitions(string directory)
    {
        (int Number, string Path)[] partitions = Directory.EnumerateFiles(directory, "store_partition_*_v1.json")
            .Select(path => (Number: ParsePartitionNumber(path), Path: path))
            .OrderBy(item => item.Number)
            .ToArray();
        if (partitions.Length == 0)
        {
            throw new InvalidDataException($"Database '{directory}' has no event partitions.");
        }

        for (int index = 0; index < partitions.Length; index++)
        {
            if (partitions[index].Number != index + 1)
            {
                throw new InvalidDataException($"Database '{directory}' has a missing or invalid partition number.");
            }
        }

        return partitions;
    }

    private static string GetPartitionPath(string directory, int number)
    {
        return Path.Combine(directory, $"store_partition_{number:000000}_v1.json");
    }

    private static int ParsePartitionNumber(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = "store_partition_";
        const string suffix = "_v1.json";
        string number = name[prefix.Length..^suffix.Length];
        return number.Length == 6 && int.TryParse(number, out int parsed) && parsed > 0
            ? parsed
            : throw new InvalidDataException($"Partition filename '{name}' is invalid.");
    }
}