using System.Text.Json;

namespace NativeDCB.Model.Events;

public sealed record SequencedEvent(
    long EventId,
    string Type,
    JsonElement Data,
    IReadOnlyList<EventKey> Keys,
    uint SchemaVersion,
    DateTimeOffset TimestampUtc,
    Guid CommandId,
    string CommandType);