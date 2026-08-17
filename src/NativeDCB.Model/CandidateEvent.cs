using System.Text.Json;

namespace NativeDCB.Model;

public sealed record CandidateEvent(
    string Type,
    JsonElement Data,
    IReadOnlyList<EventKey> Keys,
    uint SchemaVersion = 1);