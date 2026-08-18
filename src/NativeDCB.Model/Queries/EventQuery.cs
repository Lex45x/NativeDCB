using NativeDCB.Model.Events;

namespace NativeDCB.Model.Queries;

public sealed record EventQuery(IReadOnlyList<QueryItem> Items)
{
    public static EventQuery All { get; } = new([]);

    public bool Matches(SequencedEvent @event)
    {
        return Items.Count == 0 || Items.Any(item => item.Matches(@event));
    }
}