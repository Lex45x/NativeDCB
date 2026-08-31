// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Actors.Messages;

[GenerateSerializer]
[Immutable]
public sealed record AuditOperationContextMessage(
    [property: Id(0)] Guid OperationId,
    [property: Id(1)] string Category,
    [property: Id(2)] string Operation,
    [property: Id(3)] string? AuthenticationScheme,
    [property: Id(4)] string? Subject,
    [property: Id(5)] string? Issuer,
    [property: Id(6)] string? Database,
    [property: Id(7)] string? Resource,
    [property: Id(8)] string? TraceId,
    [property: Id(9)] string? CommandId);

[GenerateSerializer]
[Immutable]
public sealed record AppendAuditRecordMessage(
    [property: Id(0)] Guid OperationId,
    [property: Id(1)] string Phase,
    [property: Id(2)] string Category,
    [property: Id(3)] string Operation,
    [property: Id(4)] string? AuthenticationScheme = null,
    [property: Id(5)] string? Subject = null,
    [property: Id(6)] string? Issuer = null,
    [property: Id(7)] string? Database = null,
    [property: Id(8)] string? Resource = null,
    [property: Id(9)] string? Outcome = null,
    [property: Id(10)] string? Code = null,
    [property: Id(11)] string? GrpcStatus = null,
    [property: Id(12)] string? TraceId = null,
    [property: Id(13)] string? CommandId = null,
    [property: Id(14)] long? FirstEventId = null,
    [property: Id(15)] long? LastEventId = null,
    [property: Id(16)] long? Revision = null);

[GenerateSerializer]
[Immutable]
public sealed record AuditRecordMessage(
    [property: Id(0)] long Sequence,
    [property: Id(1)] DateTimeOffset TimestampUtc,
    [property: Id(2)] Guid OperationId,
    [property: Id(3)] string Phase,
    [property: Id(4)] string Category,
    [property: Id(5)] string Operation,
    [property: Id(6)] string? AuthenticationScheme,
    [property: Id(7)] string? Subject,
    [property: Id(8)] string? Issuer,
    [property: Id(9)] string? Database,
    [property: Id(10)] string? Resource,
    [property: Id(11)] string? Outcome,
    [property: Id(12)] string? Code,
    [property: Id(13)] string? GrpcStatus,
    [property: Id(14)] string? TraceId,
    [property: Id(15)] string? CommandId,
    [property: Id(16)] long? FirstEventId,
    [property: Id(17)] long? LastEventId,
    [property: Id(18)] long? Revision,
    [property: Id(19)] string PreviousHash,
    [property: Id(20)] string RecordHash);

[GenerateSerializer]
[Immutable]
public sealed record ListAuditRecordsActorRequest(
    [property: Id(0)] long AfterSequence,
    [property: Id(1)] int Limit,
    [property: Id(2)] string? Database,
    [property: Id(3)] string? Operation,
    [property: Id(4)] string? Phase,
    [property: Id(5)] string? Outcome,
    [property: Id(6)] string? AuthenticationScheme,
    [property: Id(7)] string? Subject);

[GenerateSerializer]
[Immutable]
public sealed record ListAuditRecordsActorResponse(
    [property: Id(0)] AuditRecordMessage[] Records,
    [property: Id(1)] long NextAfterSequence,
    [property: Id(2)] bool HasMore,
    [property: Id(3)] long BoundarySequence);

[GenerateSerializer]
[Immutable]
public sealed record AuditStatusMessage(
    [property: Id(0)] bool Ready,
    [property: Id(1)] long LastSequence,
    [property: Id(2)] int ActivePartition,
    [property: Id(3)] string? Fault);