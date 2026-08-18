using NativeDCB.Model;

// Persisted JSON contracts retain fields that are validated by external tooling.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine;

public sealed record LatestEventState(
    string EventType,
    EventKey Key,
    long EventId);