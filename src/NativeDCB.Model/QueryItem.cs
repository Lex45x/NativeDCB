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