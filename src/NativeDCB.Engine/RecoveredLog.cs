using NativeDCB.Model;

namespace NativeDCB.Engine;

internal sealed class RecoveredLog
{
    public List<SequencedEvent> Events { get; } = [];

    public Dictionary<Guid, List<SequencedEvent>> Commands { get; } = [];

    public List<RecoveredPartition> Partitions { get; } = [];

    public long Head => Events.Count == 0 ? 0 : Events[^1].EventId;
}