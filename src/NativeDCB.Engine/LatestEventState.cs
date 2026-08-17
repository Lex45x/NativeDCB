using NativeDCB.Model;

namespace NativeDCB.Engine;

public sealed record LatestEventState(
    string EventType,
    EventKey Key,
    long EventId);