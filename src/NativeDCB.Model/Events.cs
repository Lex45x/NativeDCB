using System.Text.Json;

namespace NativeDCB.Model;

public sealed record EventKey(string Name, string Value);

public sealed record CandidateEvent(
    string Type,
    JsonElement Data,
    IReadOnlyList<EventKey> Keys,
    uint SchemaVersion = 1);

public sealed record SequencedEvent(
    long EventId,
    string Type,
    JsonElement Data,
    IReadOnlyList<EventKey> Keys,
    uint SchemaVersion,
    DateTimeOffset TimestampUtc,
    Guid CommandId,
    string CommandType);

public sealed record EventBatch(
    Guid CommandId,
    string CommandType,
    IReadOnlyList<CandidateEvent> Events);