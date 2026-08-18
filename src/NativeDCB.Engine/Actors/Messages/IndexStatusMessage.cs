namespace NativeDCB.Engine.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record IndexStatusMessage(
    [property: Id(id: 0)] string EventType,
    [property: Id(id: 1)] EventKeyMessage Key,
    [property: Id(id: 2)] string FilePath,
    [property: Id(id: 3)] long IndexHead,
    [property: Id(id: 4)] long MainHead,
    [property: Id(id: 5)] IndexHydrationState HydrationState,
    [property: Id(id: 6)] string? LastFault);