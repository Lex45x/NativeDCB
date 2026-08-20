namespace NativeDCB.Actors.Messages;

public enum DecisionCallErrorKind
{
    None,
    InvalidArgument,
    NotFound,
    FailedPrecondition,
    Unavailable,
    DataLoss
}

public enum ExecuteDecisionOutcome
{
    Committed,
    AlreadyCommitted,
    Rejected,
    Failed
}

public enum PrepareDecisionOutcome
{
    Prepared,
    AlreadyCommitted,
    Failed
}

public enum CompleteDecisionOutcome
{
    Committed,
    AlreadyCommitted,
    Stale,
    Expired,
    Invalidated,
    Failed
}

[GenerateSerializer]
[Immutable]
public sealed record ExecuteDecisionMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] Guid CommandId,
    [property: Id(2)] string HandlerName,
    [property: Id(3)] byte[] CommandJson);

[GenerateSerializer]
[Immutable]
public sealed record ExecuteDecisionResultMessage(
    [property: Id(0)] ExecuteDecisionOutcome Outcome,
    [property: Id(1)] Guid CommandId,
    [property: Id(2)] string CommandType,
    [property: Id(3)] SequencedEventMessage[] Events,
    [property: Id(4)] string? Code = null,
    [property: Id(5)] string? Message = null,
    [property: Id(6)] DecisionCallErrorKind ErrorKind = DecisionCallErrorKind.None);

[GenerateSerializer]
[Immutable]
public sealed record PrepareDecisionMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] Guid CommandId,
    [property: Id(2)] string HandlerName,
    [property: Id(3)] byte[] CommandJson);

[GenerateSerializer]
[Immutable]
public sealed record PrepareDecisionResultMessage(
    [property: Id(0)] PrepareDecisionOutcome Outcome,
    [property: Id(1)] Guid CommandId,
    [property: Id(2)] string CommandType,
    [property: Id(3)] SequencedEventMessage[] Events,
    [property: Id(4)] byte[] ModelJson,
    [property: Id(5)] byte[] ModelSignature,
    [property: Id(6)] DateTimeOffset ExpiresUtc,
    [property: Id(7)] string PlanFingerprint,
    [property: Id(8)] string? Code = null,
    [property: Id(9)] string? Message = null,
    [property: Id(10)] DecisionCallErrorKind ErrorKind = DecisionCallErrorKind.None);

[GenerateSerializer]
[Immutable]
public sealed record ProposedDecisionEventMessage(
    [property: Id(0)] string Type,
    [property: Id(1)] byte[] DataJson);

[GenerateSerializer]
[Immutable]
public sealed record RouteRemoteDecisionMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] byte[] ModelSignature,
    [property: Id(2)] ProposedDecisionEventMessage[] ProposedEvents);

[GenerateSerializer]
[Immutable]
public sealed record CompleteDecisionMessage(
    [property: Id(0)] RemoteDecisionClaimsMessage Claims,
    [property: Id(1)] ProposedDecisionEventMessage[] ProposedEvents);

[GenerateSerializer]
[Immutable]
public sealed record CompleteDecisionResultMessage(
    [property: Id(0)] CompleteDecisionOutcome Outcome,
    [property: Id(1)] Guid CommandId,
    [property: Id(2)] string CommandType,
    [property: Id(3)] SequencedEventMessage[] Events,
    [property: Id(4)] long CurrentHead = 0,
    [property: Id(5)] DateTimeOffset ExpiresUtc = default,
    [property: Id(6)] string? Code = null,
    [property: Id(7)] string? Message = null,
    [property: Id(8)] DecisionCallErrorKind ErrorKind = DecisionCallErrorKind.None);

[GenerateSerializer]
[Immutable]
public sealed record RemoteDecisionClaimsMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] Guid StoreId,
    [property: Id(2)] Guid CommandId,
    [property: Id(3)] string CommandType,
    [property: Id(4)] string HandlerName,
    [property: Id(5)] string PlanFingerprint,
    [property: Id(6)] uint HandlerVersion,
    [property: Id(7)] string CommandJsonHash,
    [property: Id(8)] string ModelHash,
    [property: Id(9)] long ObservedHead,
    [property: Id(10)] EventQueryMessage Query,
    [property: Id(11)] RemoteSchemaFingerprintMessage[] SchemaFingerprints,
    [property: Id(12)] string[] ExpectedEventTypes,
    [property: Id(13)] DateTimeOffset IssuedUtc,
    [property: Id(14)] DateTimeOffset ExpiresUtc,
    [property: Id(15)] byte[] Nonce);

[GenerateSerializer]
[Immutable]
public sealed record RemoteSchemaFingerprintMessage(
    [property: Id(0)] ActorSchemaKind Kind,
    [property: Id(1)] string Name,
    [property: Id(2)] string? Fingerprint,
    [property: Id(3)] uint Version);