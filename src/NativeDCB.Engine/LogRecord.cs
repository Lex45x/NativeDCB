using System.Text.Json;

using NativeDCB.Model;

namespace NativeDCB.Engine;

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