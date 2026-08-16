namespace NativeDCB.Model;

public sealed record QueryItem(
    IReadOnlyList<string> EventTypes,
    IReadOnlyList<EventKey> Keys)
{
    public bool Matches(SequencedEvent @event)
    {
        bool typeMatches = EventTypes.Count == 0 || EventTypes.Contains(@event.Type, StringComparer.Ordinal);
        return typeMatches && Keys.All(required => @event.Keys.Contains(required));
    }
}

public sealed record EventQuery(IReadOnlyList<QueryItem> Items)
{
    public static EventQuery All { get; } = new([]);

    public bool Matches(SequencedEvent @event)
    {
        return Items.Count == 0 || Items.Any(item => item.Matches(@event));
    }
}

public sealed record AppendCondition(EventQuery Query, long AfterEventId);

public enum AppendOutcome
{
    Committed,
    Conflict,
    AlreadyCommitted
}

public sealed record AppendResult(
    AppendOutcome Outcome,
    Guid CommandId,
    IReadOnlyList<SequencedEvent> Events,
    long Head);