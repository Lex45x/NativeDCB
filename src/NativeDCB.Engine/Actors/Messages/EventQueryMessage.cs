namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record EventQueryMessage(
    [property: Id(id: 0)] QueryItemMessage[] Items);