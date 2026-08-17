namespace NativeDCB.Engine.Actors;

[GenerateSerializer]
[Immutable]
public sealed record EventQueryMessage(
    [property: Id(id: 0)] QueryItemMessage[] Items);