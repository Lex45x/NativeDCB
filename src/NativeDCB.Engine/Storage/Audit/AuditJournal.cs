using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using NativeDCB.Engine.Observability;

namespace NativeDCB.Engine.Storage.Audit;

public sealed class AuditJournal : IAsyncDisposable
{
    private const string EmptyHash = "";
    private static readonly UTF8Encoding StrictUtf8 = new(
        encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly SemaphoreSlim _gate = new(initialCount: 1, maxCount: 1);
    private readonly int _maxRecordCountPerPartition;
    private readonly string _metadataPath;
    private readonly string _root;
    private readonly TimeProvider _timeProvider;
    private int _activePartition;
    private int _activeRecordCount;
    private string? _fault;
    private bool _initialized;
    private long _lastSequence;
    private string _lastHash = EmptyHash;
    private FileStream? _lock;
    private Guid _storeId;

    internal AuditJournal(string databaseRoot, int maxRecordCountPerPartition, TimeProvider timeProvider)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databaseRoot);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxRecordCountPerPartition);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _root = Path.Combine(Path.GetFullPath(databaseRoot), ".audit");
        _metadataPath = Path.Combine(_root, "audit_v1.json");
        _maxRecordCountPerPartition = maxRecordCountPerPartition;
        _timeProvider = timeProvider;
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                ThrowIfFaulted();
                return;
            }

            Directory.CreateDirectory(_root);
            _lock = new FileStream(
                Path.Combine(_root, "audit.lock"),
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.None,
                bufferSize: 1,
                FileOptions.None);

            AuditMetadata metadata = await LoadOrCreateMetadataAsync(cancellationToken).ConfigureAwait(false);
            _storeId = metadata.StoreId;
            string[] partitions = EnumeratePartitions();
            if (partitions.Length == 0)
            {
                _activePartition = 1;
                await CreatePartitionAsync(_activePartition, EmptyHash, cancellationToken).ConfigureAwait(false);
                _activeRecordCount = 0;
            }
            else
            {
                await RecoverAsync(partitions, cancellationToken).ConfigureAwait(false);
            }

            _initialized = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (_lock is not null)
            {
                await _lock.DisposeAsync().ConfigureAwait(false);
            }

            _lock = null;
            throw;
        }
        catch (Exception exception)
        {
            _fault = exception.Message;
            if (_lock is not null)
            {
                await _lock.DisposeAsync().ConfigureAwait(false);
            }

            _lock = null;
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<AuditRecord> AppendAsync(AuditRecordDraft draft, CancellationToken cancellationToken)
    {
        ValidateDraft(draft);
        long started = Stopwatch.GetTimestamp();
        // The explicit operation name is part of the telemetry contract.
        // ReSharper disable once ExplicitCallerInfoArgument
        using Activity? activity = EngineTelemetry.Activities.StartActivity("audit.append");
        activity?.SetTag("audit.phase", draft.Phase);
        activity?.SetTag("audit.category", draft.Category);
        activity?.SetTag("audit.operation", draft.Operation);
        activity?.SetTag("db.namespace", draft.Database);
        try
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            EngineTelemetry.AuditFailures.Add(1,
                new KeyValuePair<string, object?>("operation", draft.Operation),
                new KeyValuePair<string, object?>("fault.kind", exception.GetType().Name));
            EngineTelemetry.AuditAppendDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("phase", draft.Phase));
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            if (_activeRecordCount >= _maxRecordCountPerPartition)
            {
                _activePartition = checked(_activePartition + 1);
                await CreatePartitionAsync(_activePartition, _lastHash, cancellationToken).ConfigureAwait(false);
                _activeRecordCount = 0;
            }

            AuditRecordHashInput hashInput = new(
                AuditSchemas.Record,
                checked(_lastSequence + 1),
                _timeProvider.GetUtcNow(),
                draft.OperationId,
                draft.Phase,
                draft.Category,
                draft.Operation,
                draft.AuthenticationScheme,
                draft.Subject,
                draft.Issuer,
                draft.Database,
                draft.Resource,
                draft.Outcome,
                draft.Code,
                draft.GrpcStatus,
                draft.TraceId,
                draft.CommandId,
                draft.FirstEventId,
                draft.LastEventId,
                draft.Revision,
                _lastHash);
            string hash = ComputeHash(hashInput);
            AuditRecord record = ToRecord(hashInput, hash);
            await AppendLineAsync(PartitionPath(_activePartition), record, cancellationToken).ConfigureAwait(false);
            _lastSequence = record.Sequence;
            _lastHash = record.RecordHash;
            _activeRecordCount++;
            EngineTelemetry.AuditRecords.Add(1,
                new KeyValuePair<string, object?>("phase", record.Phase),
                new KeyValuePair<string, object?>("category", record.Category),
                new KeyValuePair<string, object?>("outcome", record.Outcome));
            activity?.SetTag("audit.sequence", record.Sequence);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return record;
        }
        catch (Exception exception)
        {
            _fault = exception.Message;
            EngineTelemetry.AuditFailures.Add(1,
                new KeyValuePair<string, object?>("operation", draft.Operation),
                new KeyValuePair<string, object?>("fault.kind", exception.GetType().Name));
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            EngineTelemetry.AuditAppendDuration.Record(
                Stopwatch.GetElapsedTime(started).TotalSeconds,
                new KeyValuePair<string, object?>("phase", draft.Phase));
            _gate.Release();
        }
    }

    internal async Task<AuditPage> QueryAsync(AuditQuery query, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(query.AfterSequence);
        if (query.Limit is < 1 or > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(query), "Audit query limit must be between 1 and 1000.");
        }

        // The explicit operation name is part of the telemetry contract.
        // ReSharper disable once ExplicitCallerInfoArgument
        using Activity? activity = EngineTelemetry.Activities.StartActivity("audit.query");
        activity?.SetTag("db.namespace", query.Database);
        activity?.SetTag("audit.operation", query.Operation);
        activity?.SetTag("audit.phase", query.Phase);
        await InitializeAsync(cancellationToken).ConfigureAwait(false);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ThrowIfFaulted();
            long boundary = _lastSequence;
            long examined = query.AfterSequence;
            List<AuditRecord> records = new(query.Limit);
            long lastSequence = _lastSequence;
            string lastHash = _lastHash;
            _lastSequence = 0;
            _lastHash = EmptyHash;
            try
            {
                foreach (string path in EnumeratePartitions())
                {
                    foreach (AuditRecord record in await ReadRecordsAsync(
                                 path, repairActiveSuffix: false, cancellationToken).ConfigureAwait(false))
                    {
                        if (record.Sequence <= query.AfterSequence)
                        {
                            continue;
                        }

                        if (record.Sequence > boundary || records.Count >= query.Limit)
                        {
                            activity?.SetTag("audit.record_count", records.Count);
                            activity?.SetStatus(ActivityStatusCode.Ok);
                            return new AuditPage(records.ToArray(), examined, examined < boundary, boundary);
                        }

                        examined = record.Sequence;
                        if (Matches(record, query))
                        {
                            records.Add(record);
                        }
                    }
                }
            }
            finally
            {
                _lastSequence = lastSequence;
                _lastHash = lastHash;
            }

            activity?.SetTag("audit.record_count", records.Count);
            activity?.SetStatus(ActivityStatusCode.Ok);
            return new AuditPage(records.ToArray(), examined, examined < boundary, boundary);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            activity?.SetStatus(ActivityStatusCode.Error, "cancelled");
            throw;
        }
        catch (Exception exception)
        {
            _fault = exception.Message;
            activity?.SetStatus(ActivityStatusCode.Error, exception.Message);
            throw;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal AuditJournalStatus GetStatus()
    {
        return new AuditJournalStatus(
            _initialized && _fault is null,
            _lastSequence,
            _activePartition,
            _fault);
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_lock is not null)
            {
                await _lock.DisposeAsync().ConfigureAwait(false);
            }

            _lock = null;
            _initialized = false;
        }
        finally
        {
            _gate.Release();
            _gate.Dispose();
        }
    }

    private async Task<AuditMetadata> LoadOrCreateMetadataAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_metadataPath))
        {
            AuditMetadata created = new(
                AuditSchemas.Metadata,
                Guid.NewGuid(),
                _timeProvider.GetUtcNow(),
                _maxRecordCountPerPartition);
            await ReplaceJsonAsync(_metadataPath, created, cancellationToken).ConfigureAwait(false);
            return created;
        }

        await using FileStream stream = new(
            _metadataPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        AuditMetadata metadata = await JsonSerializer.DeserializeAsync<AuditMetadata>(
                                     stream, AuditJson.Options, cancellationToken).ConfigureAwait(false)
                                 ?? throw new InvalidDataException("The audit metadata file is empty.");
        if (!string.Equals(metadata.Schema, AuditSchemas.Metadata, StringComparison.Ordinal) ||
            metadata.StoreId == Guid.Empty || metadata.MaxRecordCountPerPartition <= 0)
        {
            throw new InvalidDataException("The audit metadata file is invalid.");
        }

        if (metadata.MaxRecordCountPerPartition != _maxRecordCountPerPartition)
        {
            metadata = metadata with { MaxRecordCountPerPartition = _maxRecordCountPerPartition };
            await ReplaceJsonAsync(_metadataPath, metadata, cancellationToken).ConfigureAwait(false);
        }

        return metadata;
    }

    private async Task RecoverAsync(string[] partitions, CancellationToken cancellationToken)
    {
        for (int index = 0; index < partitions.Length; index++)
        {
            int expectedPartition = index + 1;
            int actualPartition = ParsePartitionNumber(partitions[index]);
            if (actualPartition != expectedPartition)
            {
                throw new InvalidDataException("Audit partition numbers must be contiguous and one-based.");
            }

            bool active = index == partitions.Length - 1;
            AuditRecord[] records = await ReadRecordsAsync(
                    partitions[index], repairActiveSuffix: active, cancellationToken)
                .ConfigureAwait(false);
            _activePartition = actualPartition;
            _activeRecordCount = records.Length;
        }
    }

    private async Task<AuditRecord[]> ReadRecordsAsync(
        string path,
        bool repairActiveSuffix,
        CancellationToken cancellationToken)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        if (bytes.Length == 0)
        {
            throw new InvalidDataException($"Audit partition '{Path.GetFileName(path)}' is empty.");
        }

        if (bytes[^1] != (byte)'\n')
        {
            if (!repairActiveSuffix)
            {
                throw new InvalidDataException($"Audit partition '{Path.GetFileName(path)}' has an incomplete suffix.");
            }

            int newline = Array.LastIndexOf(bytes, (byte)'\n');
            if (newline < 0)
            {
                throw new InvalidDataException($"Audit partition '{Path.GetFileName(path)}' has no complete header.");
            }

            await using FileStream repair = new(
                path, FileMode.Open, FileAccess.Write, FileShare.None, 1,
                FileOptions.Asynchronous | FileOptions.WriteThrough);
            repair.SetLength(newline + 1L);
            await repair.FlushAsync(cancellationToken).ConfigureAwait(false);
            repair.Flush(flushToDisk: true);
            bytes = bytes[..(newline + 1)];
        }

        string text;
        try
        {
            text = StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException("The audit partition is not valid UTF-8.", exception);
        }

        string[] lines = text.Split('\n');
        if (lines.Length < 2 || lines[^1].Length != 0)
        {
            throw new InvalidDataException("The audit partition header is missing.");
        }

        lines = lines[..^1];
        if (lines.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidDataException("The audit partition contains a blank line.");
        }

        AuditPartitionHeader header = Deserialize<AuditPartitionHeader>(lines[0], "header");
        int partitionNumber = ParsePartitionNumber(path);
        if (!string.Equals(header.Schema, AuditSchemas.Partition, StringComparison.Ordinal) ||
            header.StoreId != _storeId || header.PartitionNumber != partitionNumber ||
            !string.Equals(header.PreviousRecordHash, _lastHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Audit partition '{Path.GetFileName(path)}' has an invalid identity.");
        }

        List<AuditRecord> records = new(lines.Length - 1);
        for (int lineIndex = 1; lineIndex < lines.Length; lineIndex++)
        {
            AuditRecord record = Deserialize<AuditRecord>(lines[lineIndex], "record");
            ValidateRecord(record);
            _lastSequence = record.Sequence;
            _lastHash = record.RecordHash;
            records.Add(record);
        }

        return records.ToArray();
    }

    private void ValidateRecord(AuditRecord record)
    {
        if (!string.Equals(record.Schema, AuditSchemas.Record, StringComparison.Ordinal) ||
            record.Sequence != _lastSequence + 1 || record.OperationId == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.Phase) || string.IsNullOrWhiteSpace(record.Category) ||
            string.IsNullOrWhiteSpace(record.Operation) ||
            !string.Equals(record.PreviousHash, _lastHash, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The audit journal contains an invalid record.");
        }

        AuditRecordHashInput hashInput = ToHashInput(record);
        string actual = ComputeHash(hashInput);
        if (!string.Equals(record.RecordHash, actual, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The audit journal hash chain is invalid.");
        }
    }

    private async Task CreatePartitionAsync(
        int partitionNumber,
        string previousHash,
        CancellationToken cancellationToken)
    {
        string path = PartitionPath(partitionNumber);
        if (File.Exists(path))
        {
            throw new InvalidDataException($"Audit partition '{Path.GetFileName(path)}' already exists.");
        }

        AuditPartitionHeader header = new(
            AuditSchemas.Partition,
            _storeId,
            partitionNumber,
            previousHash);
        await CreateFileAsync(path, header, cancellationToken).ConfigureAwait(false);
    }

    private static async Task AppendLineAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(value, AuditJson.Options);
        await using FileStream stream = new(
            path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(json, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        stream.Flush(flushToDisk: true);
    }

    private static async Task CreateFileAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, AuditJson.Options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static async Task ReplaceJsonAsync<T>(string path, T value, CancellationToken cancellationToken)
    {
        string temporary = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (FileStream stream = new(
                             temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                             FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(stream, value, AuditJson.Options, cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private string[] EnumeratePartitions()
    {
        return Directory.EnumerateFiles(_root, "audit_partition_*_v1.ndjson", SearchOption.TopDirectoryOnly)
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    private string PartitionPath(int partitionNumber)
    {
        return Path.Combine(_root, $"audit_partition_{partitionNumber:D6}_v1.ndjson");
    }

    private static int ParsePartitionNumber(string path)
    {
        string name = Path.GetFileName(path);
        const string prefix = "audit_partition_";
        const string suffix = "_v1.ndjson";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) ||
            !name.EndsWith(suffix, StringComparison.Ordinal) ||
            !int.TryParse(name.AsSpan(prefix.Length, name.Length - prefix.Length - suffix.Length), out int value) ||
            value <= 0)
        {
            throw new InvalidDataException($"Audit partition filename '{name}' is invalid.");
        }

        return value;
    }

    private static T Deserialize<T>(string json, string kind)
    {
        try
        {
            T value = JsonSerializer.Deserialize<T>(json, AuditJson.Options)
                      ?? throw new InvalidDataException($"The audit {kind} is empty.");
            string canonical = JsonSerializer.Serialize(value, AuditJson.Options);
            if (!string.Equals(json, canonical, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"The audit {kind} is not canonical JSON.");
            }

            return value;
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException($"The audit {kind} is invalid JSON.", exception);
        }
    }

    private static string ComputeHash(AuditRecordHashInput input)
    {
        byte[] canonical = JsonSerializer.SerializeToUtf8Bytes(input, AuditJson.Options);
        return Convert.ToHexStringLower(SHA256.HashData(canonical));
    }

    private static AuditRecord ToRecord(AuditRecordHashInput input, string hash)
    {
        return new AuditRecord(
            input.Schema,
            input.Sequence,
            input.TimestampUtc,
            input.OperationId,
            input.Phase,
            input.Category,
            input.Operation,
            input.AuthenticationScheme,
            input.Subject,
            input.Issuer,
            input.Database,
            input.Resource,
            input.Outcome,
            input.Code,
            input.GrpcStatus,
            input.TraceId,
            input.CommandId,
            input.FirstEventId,
            input.LastEventId,
            input.Revision,
            input.PreviousHash,
            hash);
    }

    private static AuditRecordHashInput ToHashInput(AuditRecord record)
    {
        return new AuditRecordHashInput(
            record.Schema,
            record.Sequence,
            record.TimestampUtc,
            record.OperationId,
            record.Phase,
            record.Category,
            record.Operation,
            record.AuthenticationScheme,
            record.Subject,
            record.Issuer,
            record.Database,
            record.Resource,
            record.Outcome,
            record.Code,
            record.GrpcStatus,
            record.TraceId,
            record.CommandId,
            record.FirstEventId,
            record.LastEventId,
            record.Revision,
            record.PreviousHash);
    }

    private static bool Matches(AuditRecord record, AuditQuery query)
    {
        return Exact(query.Database, record.Database) &&
               Exact(query.Operation, record.Operation) &&
               Exact(query.Phase, record.Phase) &&
               Exact(query.Outcome, record.Outcome) &&
               Exact(query.AuthenticationScheme, record.AuthenticationScheme) &&
               Exact(query.Subject, record.Subject);
    }

    private static bool Exact(string? filter, string? value)
    {
        return filter is null || string.Equals(filter, value, StringComparison.Ordinal);
    }

    private static void ValidateDraft(AuditRecordDraft draft)
    {
        if (draft.OperationId == Guid.Empty || string.IsNullOrWhiteSpace(draft.Phase) ||
            string.IsNullOrWhiteSpace(draft.Category) || string.IsNullOrWhiteSpace(draft.Operation))
        {
            throw new ArgumentException("Audit records require an operation ID, phase, category, and operation.",
                nameof(draft));
        }
    }

    private void ThrowIfFaulted()
    {
        if (_fault is not null)
        {
            throw new InvalidOperationException($"The audit journal is faulted: {_fault}");
        }
    }
}