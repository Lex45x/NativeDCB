using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Grains;

internal static class IndexQueryCombinationKernel
{
    internal static Dictionary<long, SequencedEventMessage> Combine(
        string database,
        EventQueryMessage query,
        IReadOnlyDictionary<string, IndexSnapshotMessage> snapshots,
        long throughEventIdInclusive)
    {
        Dictionary<long, SequencedEventMessage> results = new();
        foreach (QueryItemMessage item in query.Items)
        {
            foreach (string eventType in item.EventTypes)
            {
                Dictionary<long, SequencedEventMessage>? intersection = null;
                foreach (EventKeyMessage key in item.Keys)
                {
                    IndexSnapshotMessage snapshot = snapshots[IndexIdentity.Encode(database, eventType, key)];
                    long through = Math.Min(throughEventIdInclusive, snapshot.Head);
                    Dictionary<long, SequencedEventMessage> current = snapshot.Events
                        .Where(value => value.EventId <= through)
                        .ToDictionary(value => value.EventId);
                    if (intersection is null)
                    {
                        intersection = current;
                        continue;
                    }

                    foreach (long eventId in intersection.Keys.Except(current.Keys).ToArray())
                    {
                        intersection.Remove(eventId);
                    }
                }

                foreach ((long eventId, SequencedEventMessage value) in intersection ?? [])
                {
                    results[eventId] = value;
                }
            }
        }

        return results;
    }
}