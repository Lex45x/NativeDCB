namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record IndexListMessage(
    [property: Id(id: 0)] IndexStatusMessage[] Indexes);