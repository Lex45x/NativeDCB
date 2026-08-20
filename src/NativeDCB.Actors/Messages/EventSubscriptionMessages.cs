namespace NativeDCB.Actors.Messages;

public enum EventSubscriptionKind
{
    Range = 0,
    Query = 1
}

[GenerateSerializer]
[Immutable]
public sealed record EventSubscriptionOpenMessage(
    [property: Id(id: 0)] string Database,
    [property: Id(id: 1)] EventSubscriptionKind Kind,
    [property: Id(id: 2)] EventQueryMessage Query,
    [property: Id(id: 3)] EventKeyMessage[] RequiredKeys,
    [property: Id(id: 4)] long AfterEventId,
    [property: Id(id: 5)] int? Limit);

[GenerateSerializer]
[Immutable]
public sealed record EventSubscriptionReadNextMessage(
    [property: Id(id: 0)] int MaxCount);

[GenerateSerializer]
[Immutable]
public sealed record EventSubscriptionReadResultMessage(
    [property: Id(id: 0)] long ObservedHead,
    [property: Id(id: 1)] SequencedEventMessage[] Events,
    [property: Id(id: 2)] bool Completed);

[GenerateSerializer]
[Immutable]
public sealed record EventSubscriptionCloseMessage;