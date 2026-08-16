using System.Text.Json;
using System.Text.Json.Serialization;

using NativeDCB.Model;

// Persisted JSON contracts retain fields that are validated by external tooling.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine;

public sealed record PartitionStatus(
    int PartitionNumber,
    string FilePath,
    bool IsActive,
    long? FirstEventId,
    long? LastEventId,
    int CommittedEventCount,
    long Length);

public sealed record CommandState(
    Guid CommandId,
    long FirstEventId,
    long LastEventId,
    int EventCount);

public sealed record LatestEventState(
    string EventType,
    EventKey Key,
    long EventId);

public sealed record PartitionCheckpoint(
    int PartitionNumber,
    long? FirstEventId,
    long? LastEventId,
    int CommittedEventCount,
    long Length,
    string ContentFingerprint);

public sealed record StateFileModel(
    int FormatVersion,
    Guid StoreId,
    int PartitionNumber,
    long Head,
    long CommittedEventCount,
    IReadOnlyList<SequencedEvent> Events,
    IReadOnlyList<long> KnownEventIds,
    IReadOnlyList<CommandState> Commands,
    IReadOnlyList<LatestEventState> LatestEvents,
    IReadOnlyList<PartitionCheckpoint> Partitions,
    string EventSnapshotFingerprint,
    string EventSchemaFingerprint,
    string KeyEncodingFingerprint,
    string StateSchemaFingerprint);

internal sealed record DatabaseMetadata(
    int FormatVersion,
    Guid StoreId,
    DateTimeOffset CreatedUtc);

internal sealed class LogRecord
{
    public string? Kind { get; init; }

    public int? FormatVersion { get; init; }

    public Guid? StoreId { get; init; }

    public int? PartitionNumber { get; init; }

    public long? EventId { get; init; }

    public Guid? BatchId { get; init; }

    public int? BatchIndex { get; init; }

    public int? BatchCount { get; init; }

    public string? Type { get; init; }

    public uint? SchemaVersion { get; init; }

    public IReadOnlyList<EventKey>? Keys { get; init; }

    public JsonElement? Data { get; init; }

    public DateTimeOffset? TimestampUtc { get; init; }

    public Guid? CommandId { get; init; }

    public string? CommandType { get; init; }

    public long? FirstEventId { get; init; }

    public long? LastEventId { get; init; }
}

internal static class StorageJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };
}