namespace NativeDCB.Engine;

public sealed record StateFileInspection(
    bool Present,
    bool Locked,
    bool JsonValid,
    bool SchemaValid,
    int SourcePartition,
    string? Error);