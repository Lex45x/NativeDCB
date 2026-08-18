namespace NativeDCB.Engine;

public sealed record CommandState(
    Guid CommandId,
    long FirstEventId,
    long LastEventId,
    int EventCount);