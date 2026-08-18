namespace NativeDCB.Engine.Storage.State;

public sealed record StateFileInspection(
    bool Present,
    bool Locked,
    bool JsonValid,
    bool SchemaValid,
    int SourcePartition,
    string? Error);