using NativeDCB.Model.Queries;

namespace NativeDCB.Server.Decisions.Remote;

internal sealed record RemoteDecisionClaims(
    string Database,
    Guid StoreId,
    Guid CommandId,
    string CommandType,
    string HandlerName,
    string PlanFingerprint,
    string CommandJsonHash,
    string ModelHash,
    long ObservedHead,
    EventQuery Query,
    RemoteSchemaFingerprint[] SchemaFingerprints,
    string[] ExpectedEventTypes,
    DateTimeOffset IssuedUtc,
    DateTimeOffset ExpiresUtc,
    byte[] Nonce);

internal sealed record RemoteSchemaFingerprint(string Kind, string Name, string? Fingerprint);