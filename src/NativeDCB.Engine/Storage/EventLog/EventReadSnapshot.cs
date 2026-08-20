using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.EventLog;

public sealed record EventReadSnapshot(
    long ObservedHead,
    IReadOnlyList<SequencedEvent> Events);