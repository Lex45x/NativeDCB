namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record CandidateEventMessage(
    [property: Id(id: 0)] string Type,
    [property: Id(id: 1)] string DataJson,
    [property: Id(id: 2)] EventKeyMessage[] Keys,
    [property: Id(id: 3)] uint SchemaVersion);