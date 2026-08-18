namespace NativeDCB.Engine.Storage.State;

public sealed record CommandState(
    Guid CommandId,
    long FirstEventId,
    long LastEventId,
    int EventCount);