// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Actors.Messages;

public enum ActorSchemaKind
{
    Unspecified,
    Event,
    Command
}

public enum ActorDiagnosticSeverity
{
    Unspecified,
    Info,
    Warning,
    Error
}

public enum CatalogErrorKind
{
    None,
    InvalidArgument
}

[GenerateSerializer]
[Immutable]
public sealed record CatalogErrorMessage(
    [property: Id(0)] CatalogErrorKind Kind,
    [property: Id(1)] string Message);

[GenerateSerializer]
[Immutable]
public sealed record ActorSourcePositionMessage(
    [property: Id(0)] uint Line,
    [property: Id(1)] uint Column,
    [property: Id(2)] uint Offset);

[GenerateSerializer]
[Immutable]
public sealed record ActorSourceSpanMessage(
    [property: Id(0)] ActorSourcePositionMessage Start,
    [property: Id(1)] ActorSourcePositionMessage End);

[GenerateSerializer]
[Immutable]
public sealed record ActorDiagnosticMessage(
    [property: Id(0)] string Code,
    [property: Id(1)] ActorDiagnosticSeverity Severity,
    [property: Id(2)] string Message,
    [property: Id(3)] ActorSourceSpanMessage? SourceSpan = null);

[GenerateSerializer]
[Immutable]
public sealed record RegisterSchemaMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] ActorSchemaKind Kind,
    [property: Id(2)] string DocumentJson,
    [property: Id(3)] bool AllowIncompatible);

[GenerateSerializer]
[Immutable]
public sealed record SchemaRegistrationResultMessage(
    [property: Id(0)] string Fingerprint,
    [property: Id(1)] uint Version,
    [property: Id(2)] bool Registered,
    [property: Id(3)] ActorDiagnosticMessage[] Diagnostics,
    [property: Id(4)] CatalogErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record SchemaRegistrationMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] ActorSchemaKind Kind,
    [property: Id(2)] string DocumentJson,
    [property: Id(3)] string Fingerprint,
    [property: Id(4)] uint Version);

[GenerateSerializer]
[Immutable]
public sealed record SchemaLookupMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] ActorSchemaKind Kind);

[GenerateSerializer]
[Immutable]
public sealed record SchemaRemoveResultMessage(
    [property: Id(0)] string Fingerprint,
    [property: Id(1)] uint Version);

[GenerateSerializer]
[Immutable]
public sealed record SchemaListMessage(
    [property: Id(0)] SchemaRegistrationMessage[] Schemas,
    [property: Id(1)] uint Revision);

[GenerateSerializer]
[Immutable]
public sealed record SchemaSnapshotMessage(
    [property: Id(0)] SchemaRegistrationMessage[] EventSchemas,
    [property: Id(1)] SchemaRegistrationMessage[] CommandSchemas,
    [property: Id(2)] uint Revision);

[GenerateSerializer]
[Immutable]
public sealed record RegisterHandlerMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] string CommandType,
    [property: Id(2)] string NdlSource,
    [property: Id(3)] string PlanJson,
    [property: Id(4)] bool AllowIncompatible);

[GenerateSerializer]
[Immutable]
public sealed record HandlerDescriptionMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] string CommandType,
    [property: Id(2)] string NdlSource,
    [property: Id(3)] string SourceFingerprint,
    [property: Id(4)] string PlanFingerprint,
    [property: Id(5)] bool Valid,
    [property: Id(6)] ActorDiagnosticMessage[] Diagnostics,
    [property: Id(7)] string? PlanJson,
    [property: Id(8)] string? GeneratedNdl,
    [property: Id(9)] ActorDiagnosticMessage[] NdlGenerationDiagnostics,
    [property: Id(10)] uint Version);

[GenerateSerializer]
[Immutable]
public sealed record HandlerRegistrationResultMessage(
    [property: Id(0)] HandlerDescriptionMessage Handler,
    [property: Id(1)] CatalogErrorMessage? Error = null);

[GenerateSerializer]
[Immutable]
public sealed record GetHandlerMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] bool IncludePlanJson,
    [property: Id(2)] bool GenerateNdl);

[GenerateSerializer]
[Immutable]
public sealed record HandlerRemoveResultMessage(
    [property: Id(0)] string SourceFingerprint,
    [property: Id(1)] string PlanFingerprint,
    [property: Id(2)] uint Version);

[GenerateSerializer]
[Immutable]
public sealed record HandlerSummaryMessage(
    [property: Id(0)] string Name,
    [property: Id(1)] string CommandType,
    [property: Id(2)] string SourceFingerprint,
    [property: Id(3)] string PlanFingerprint,
    [property: Id(4)] bool Valid,
    [property: Id(5)] uint Version);

[GenerateSerializer]
[Immutable]
public sealed record HandlerListMessage(
    [property: Id(0)] HandlerSummaryMessage[] Handlers,
    [property: Id(1)] uint Revision);

[GenerateSerializer]
[Immutable]
public sealed record TransientSchemaMessage(
    [property: Id(0)] ActorSchemaKind Kind,
    [property: Id(1)] string Name,
    [property: Id(2)] string DocumentJson);

[GenerateSerializer]
[Immutable]
public sealed record ValidateNdlMessage(
    [property: Id(0)] string NdlSource,
    [property: Id(1)] TransientSchemaMessage[] TransientSchemas);

[GenerateSerializer]
[Immutable]
public sealed record PlanSummaryMessage(
    [property: Id(0)] string Fingerprint,
    [property: Id(1)] string RedactedSummary,
    [property: Id(2)] string[] Operations);

[GenerateSerializer]
[Immutable]
public sealed record NdlValidationResultMessage(
    [property: Id(0)] bool Valid,
    [property: Id(1)] ActorDiagnosticMessage[] Diagnostics,
    [property: Id(2)] PlanSummaryMessage? Plan,
    [property: Id(3)] CatalogErrorMessage? Error = null,
    [property: Id(4)] EventQueryMessage[]? QueryTemplates = null);

[GenerateSerializer]
[Immutable]
public sealed record PublishStatementMessage([property: Id(0)] string NdlSource);

[GenerateSerializer]
[Immutable]
public sealed record PublishStatementResultMessage(
    [property: Id(0)] bool Valid,
    [property: Id(1)] ActorDiagnosticMessage[] Diagnostics,
    [property: Id(2)] HandlerSummaryMessage[] Registrations,
    [property: Id(3)] CatalogErrorMessage? Error = null);