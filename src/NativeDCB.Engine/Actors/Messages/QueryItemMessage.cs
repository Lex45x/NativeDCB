namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record QueryItemMessage(
    [property: Id(id: 0)] string[] EventTypes,
    [property: Id(id: 1)] EventKeyMessage[] Keys);