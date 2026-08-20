namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record EventKeyMessage(
    [property: Id(id: 0)] string Name,
    [property: Id(id: 1)] string Value);