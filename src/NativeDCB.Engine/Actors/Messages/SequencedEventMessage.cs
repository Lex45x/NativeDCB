namespace NativeDCB.Engine.Actors;

[GenerateSerializer]
[Immutable]
public sealed record SequencedEventMessage(
    [property: Id(id: 0)] long EventId,
    [property: Id(id: 1)] string Type,
    [property: Id(id: 2)] string DataJson,
    [property: Id(id: 3)] EventKeyMessage[] Keys,
    [property: Id(id: 4)] uint SchemaVersion,
    [property: Id(id: 5)] DateTimeOffset TimestampUtc,
    [property: Id(id: 6)] Guid CommandId,
    [property: Id(id: 7)] string CommandType);