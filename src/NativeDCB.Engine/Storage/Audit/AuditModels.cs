namespace NativeDCB.Engine.Storage.Audit;

internal static class AuditSchemas
{
    public const string Metadata = "native-dcb-audit-v1";
    public const string Partition = "native-dcb-audit-partition-v1";
    public const string Record = "native-dcb-audit-record-v1";
}

internal sealed record AuditRecordDraft(
    Guid OperationId,
    string Phase,
    string Category,
    string Operation,
    string? AuthenticationScheme = null,
    string? Subject = null,
    string? Issuer = null,
    string? Database = null,
    string? Resource = null,
    string? Outcome = null,
    string? Code = null,
    string? GrpcStatus = null,
    string? TraceId = null,
    string? CommandId = null,
    long? FirstEventId = null,
    long? LastEventId = null,
    long? Revision = null);

internal sealed record AuditRecord(
    string Schema,
    long Sequence,
    DateTimeOffset TimestampUtc,
    Guid OperationId,
    string Phase,
    string Category,
    string Operation,
    string? AuthenticationScheme,
    string? Subject,
    string? Issuer,
    string? Database,
    string? Resource,
    string? Outcome,
    string? Code,
    string? GrpcStatus,
    string? TraceId,
    string? CommandId,
    long? FirstEventId,
    long? LastEventId,
    long? Revision,
    string PreviousHash,
    string RecordHash);

internal sealed record AuditQuery(
    long AfterSequence,
    int Limit,
    string? Database = null,
    string? Operation = null,
    string? Phase = null,
    string? Outcome = null,
    string? AuthenticationScheme = null,
    string? Subject = null);

internal sealed record AuditPage(
    AuditRecord[] Records,
    long NextAfterSequence,
    bool HasMore,
    long BoundarySequence);

internal sealed record AuditJournalStatus(
    bool Ready,
    long LastSequence,
    int ActivePartition,
    string? Fault);

internal sealed record AuditMetadata(
    string Schema,
    Guid StoreId,
    DateTimeOffset CreatedUtc,
    int MaxRecordCountPerPartition);

internal sealed record AuditPartitionHeader(
    string Schema,
    Guid StoreId,
    int PartitionNumber,
    string PreviousRecordHash);

internal sealed record AuditRecordHashInput(
    string Schema,
    long Sequence,
    DateTimeOffset TimestampUtc,
    Guid OperationId,
    string Phase,
    string Category,
    string Operation,
    string? AuthenticationScheme,
    string? Subject,
    string? Issuer,
    string? Database,
    string? Resource,
    string? Outcome,
    string? Code,
    string? GrpcStatus,
    string? TraceId,
    string? CommandId,
    long? FirstEventId,
    long? LastEventId,
    long? Revision,
    string PreviousHash);