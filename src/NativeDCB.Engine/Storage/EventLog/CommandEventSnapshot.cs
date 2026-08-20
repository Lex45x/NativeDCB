using NativeDCB.Model.Events;

namespace NativeDCB.Engine.Storage.EventLog;

public sealed record CommandEventSnapshot(
    Guid StoreId,
    long Head,
    IReadOnlyList<SequencedEvent> Events);