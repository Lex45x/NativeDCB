using NativeDCB.Model;
using NativeDCB.Model.Events;

// Persisted JSON contracts retain fields that are validated by external tooling.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine.Storage.State;

public sealed record LatestEventState(
    string EventType,
    EventKey Key,
    long EventId);