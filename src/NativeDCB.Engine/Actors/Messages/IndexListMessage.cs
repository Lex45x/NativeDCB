namespace NativeDCB.Engine.Actors;

[GenerateSerializer]
[Immutable]
public sealed record IndexListMessage(
    [property: Id(id: 0)] IndexStatusMessage[] Indexes);