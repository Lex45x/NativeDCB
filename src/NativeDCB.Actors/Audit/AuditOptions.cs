namespace NativeDCB.Actors.Audit;

public sealed class AuditOptions
{
    public int MaxRecordCountPerPartition { get; init; } = 10_000;
}