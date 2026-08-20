using NativeDCB.Model.Databases;

// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Actors.Messages;

public enum DatabaseDirectoryErrorKind
{
    None,
    InvalidArgument,
    NotFound,
    AlreadyExists,
    Unavailable,
    DataLoss
}

[GenerateSerializer]
[Immutable]
public sealed record DatabaseDirectoryErrorMessage(
    [property: Id(0)] DatabaseDirectoryErrorKind Kind,
    [property: Id(1)] string Message);

[GenerateSerializer]
[Immutable]
public sealed record DatabaseSummaryMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] DatabaseStatus Status,
    [property: Id(2)] long MainHead,
    [property: Id(3)] bool ReadAvailable,
    [property: Id(4)] bool WriteAvailable,
    [property: Id(5)] string? Fault);

[GenerateSerializer]
[Immutable]
public sealed record CatalogFingerprintMessage(
    [property: Id(0)] string Kind,
    [property: Id(1)] string Name,
    [property: Id(2)] string Fingerprint);

[GenerateSerializer]
[Immutable]
public sealed record ActorDatabaseInfoMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] DatabaseStatus Status,
    [property: Id(2)] string DatabaseVersion,
    [property: Id(3)] string FileFormatVersion,
    [property: Id(4)] long MainHead,
    [property: Id(5)] int ActivePartitionIndex,
    [property: Id(6)] bool WriterLockOwned,
    [property: Id(7)] CatalogFingerprintMessage[] CatalogFingerprints,
    [property: Id(8)] bool ReadAvailable,
    [property: Id(9)] bool WriteAvailable,
    [property: Id(10)] string? LastFault);

[GenerateSerializer]
[Immutable]
public sealed record DatabaseHealthMessage(
    [property: Id(0)] string Database,
    [property: Id(1)] DatabaseStatus Status,
    [property: Id(2)] bool Live,
    [property: Id(3)] bool ReadReady,
    [property: Id(4)] bool WriteReady,
    [property: Id(5)] string? Fault);

[GenerateSerializer]
[Immutable]
public sealed record CapabilityLimitMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] ulong Value);

[GenerateSerializer]
[Immutable]
public sealed record ListDatabasesActorRequest;

[GenerateSerializer]
[Immutable]
public sealed record ListDatabasesActorResponse(
    [property: Id(0)] DatabaseSummaryMessage[] Databases);

[GenerateSerializer]
[Immutable]
public sealed record CreateDatabaseActorRequest([property: Id(0)] string Database);

[GenerateSerializer]
[Immutable]
public sealed record CreateDatabaseActorResponse(
    [property: Id(0)] ActorDatabaseInfoMessage? Database,
    [property: Id(1)] DatabaseDirectoryErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record GetDatabaseInfoActorRequest([property: Id(0)] string Database);

[GenerateSerializer]
[Immutable]
public sealed record GetDatabaseInfoActorResponse(
    [property: Id(0)] ActorDatabaseInfoMessage? Database,
    [property: Id(1)] DatabaseDirectoryErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record GetHealthActorRequest;

[GenerateSerializer]
[Immutable]
public sealed record GetHealthActorResponse(
    [property: Id(0)] bool Live,
    [property: Id(1)] DatabaseHealthMessage[] Databases);

[GenerateSerializer]
[Immutable]
public sealed record GetCapabilitiesActorRequest;

[GenerateSerializer]
[Immutable]
public sealed record GetCapabilitiesActorResponse(
    [property: Id(0)] string ProtocolVersion,
    [property: Id(1)] string NdlVersion,
    [property: Id(2)] string[] FileFormatVersions,
    [property: Id(3)] string[] QueryFeatures,
    [property: Id(4)] bool SubscriptionsSupported,
    [property: Id(5)] CapabilityLimitMessage[] Limits);

[GenerateSerializer]
[Immutable]
public sealed record GetMainHeadActorRequest([property: Id(0)] string Database);

[GenerateSerializer]
[Immutable]
public sealed record GetMainHeadActorResponse(
    [property: Id(0)] long EventId,
    [property: Id(1)] DatabaseDirectoryErrorMessage? Error = null);