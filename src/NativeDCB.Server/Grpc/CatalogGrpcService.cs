using Google.Protobuf;

using Grpc.Core;

using NativeDCB.Actors.Audit;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

using ProtocolDiagnosticSeverity = NativeDCB.Protocol.V1.DiagnosticSeverity;

namespace NativeDCB.Server.Grpc;

public sealed class CatalogGrpcService(IGrainFactory grains) : CatalogService.CatalogServiceBase
{
    public override async Task<RegisterSchemaResponse> RegisterEventSchema(
        RegisterSchemaRequest request,
        ServerCallContext context)
    {
        return await RegisterSchemaAsync(request, ActorSchemaKind.Event, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task<RegisterSchemaResponse> RegisterCommandSchema(
        RegisterSchemaRequest request,
        ServerCallContext context)
    {
        return await RegisterSchemaAsync(request, ActorSchemaKind.Command, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task<RemoveSchemaResponse> RemoveSchema(
        RemoveSchemaRequest request,
        ServerCallContext context)
    {
        await ValidateAuditedDatabaseAsync(request.Database).ConfigureAwait(false);
        ActorSchemaKind kind;
        try
        {
            kind = ToActorKind(request.SchemaKind);
        }
        catch (RpcException exception)
        {
            await AuditRecorder.OutcomeAsync(grains, "invalid", exception.StatusCode.ToString())
                .ConfigureAwait(false);
            throw;
        }

        ISchemaGrain grain = grains.GetGrain<ISchemaGrain>(request.Database);
        SchemaRemoveResultMessage? result = await CallAsync(
                token => grain.RemoveAsync(
                    new SchemaLookupMessage(request.SchemaName, kind), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (result is null)
        {
            throw ProtocolMapper.NotFound($"Schema '{request.SchemaName}' was not found.");
        }

        return new RemoveSchemaResponse { RemovedFingerprint = result.Fingerprint };
    }

    public override async Task<GetSchemaResponse> GetSchema(
        GetSchemaRequest request,
        ServerCallContext context)
    {
        ISchemaGrain grain = grains.GetGrain<ISchemaGrain>(request.Database);
        SchemaRegistrationMessage? result = await CallAsync(
                token => grain.GetAsync(
                    new SchemaLookupMessage(request.SchemaName, ToActorKind(request.SchemaKind)), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (result is null)
        {
            throw ProtocolMapper.NotFound($"Schema '{request.SchemaName}' was not found.");
        }

        return new GetSchemaResponse { Schema = ToSchema(result) };
    }

    public override async Task<ListSchemasResponse> ListSchemas(
        ListSchemasRequest request,
        ServerCallContext context)
    {
        ISchemaGrain grain = grains.GetGrain<ISchemaGrain>(request.Database);
        SchemaListMessage result = await CallAsync(
                token => grain.ListAsync(ToActorListKind(request.SchemaKind), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ListSchemasResponse response = new();
        response.Schemas.AddRange(result.Schemas.Select(ToSchemaSummary));
        return response;
    }

    public override async Task<RegisterHandlerResponse> RegisterHandler(
        RegisterHandlerRequest request,
        ServerCallContext context)
    {
        IHandlerGrain grain = grains.GetGrain<IHandlerGrain>(request.Database);
        HandlerRegistrationResultMessage result = await CallAsync(
                token => grain.RegisterAsync(new RegisterHandlerMessage(
                    request.HandlerName,
                    request.CommandType,
                    request.NdlSource,
                    ProtocolMapper.Utf8Text(request.PlanJson),
                    request.AllowIncompatible), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new RegisterHandlerResponse { Handler = ToHandler(result.Handler) };
    }

    public override async Task<RemoveHandlerResponse> RemoveHandler(
        RemoveHandlerRequest request,
        ServerCallContext context)
    {
        IHandlerGrain grain = grains.GetGrain<IHandlerGrain>(request.Database);
        HandlerRemoveResultMessage? result = await CallAsync(
                token => grain.RemoveAsync(request.HandlerName, token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (result is null)
        {
            throw ProtocolMapper.NotFound($"Handler '{request.HandlerName}' was not found.");
        }

        return new RemoveHandlerResponse
        {
            RemovedSourceFingerprint = result.SourceFingerprint,
            RemovedPlanFingerprint = result.PlanFingerprint
        };
    }

    public override async Task<GetHandlerResponse> GetHandler(
        GetHandlerRequest request,
        ServerCallContext context)
    {
        IHandlerGrain grain = grains.GetGrain<IHandlerGrain>(request.Database);
        HandlerDescriptionMessage? result = await CallAsync(
                token => grain.GetAsync(new GetHandlerMessage(
                    request.HandlerName, request.IncludePlanJson, request.GenerateNdl), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (result is null)
        {
            throw ProtocolMapper.NotFound($"Handler '{request.HandlerName}' was not found.");
        }

        return new GetHandlerResponse { Handler = ToHandler(result) };
    }

    public override async Task<ListHandlersResponse> ListHandlers(
        ListHandlersRequest request,
        ServerCallContext context)
    {
        IHandlerGrain grain = grains.GetGrain<IHandlerGrain>(request.Database);
        HandlerListMessage result = await CallAsync(
                grain.ListAsync,
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ListHandlersResponse response = new();
        response.Handlers.AddRange(result.Handlers.Select(ToHandlerSummary));
        return response;
    }

    public override async Task<ValidateNdlResponse> ValidateNdl(
        ValidateNdlRequest request,
        ServerCallContext context)
    {
        IHandlerGrain grain = grains.GetGrain<IHandlerGrain>(request.Database);
        NdlValidationResultMessage result = await CallAsync(
                token => grain.ValidateNdlAsync(new ValidateNdlMessage(
                    request.NdlSource,
                    request.TransientSchemas.Select(value => new TransientSchemaMessage(
                        ToActorListKind(value.SchemaKind),
                        value.SchemaName,
                        ProtocolMapper.Utf8Text(value.SchemaDocumentJson))).ToArray()), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);

        ValidateNdlResponse response = new() { Valid = result.Valid };
        response.Diagnostics.AddRange(result.Diagnostics.Select(ToDiagnostic));
        if (result.Plan is not null)
        {
            response.Plan = new PlanSummary
            {
                PlanFingerprint = result.Plan.Fingerprint,
                RedactedSummary = result.Plan.RedactedSummary
            };
            response.Plan.Operations.AddRange(result.Plan.Operations);
        }

        return response;
    }

    private async Task<RegisterSchemaResponse> RegisterSchemaAsync(
        RegisterSchemaRequest request,
        ActorSchemaKind kind,
        CancellationToken cancellationToken)
    {
        await ValidateAuditedDatabaseAsync(request.Database).ConfigureAwait(false);
        ISchemaGrain grain = grains.GetGrain<ISchemaGrain>(request.Database);
        SchemaRegistrationResultMessage result = await CallAsync(
                token => grain.RegisterAsync(new RegisterSchemaMessage(
                    request.SchemaName,
                    kind,
                    ProtocolMapper.Utf8Text(request.SchemaDocumentJson),
                    request.AllowIncompatible), token),
                cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        RegisterSchemaResponse response = new() { Fingerprint = result.Fingerprint };
        response.Diagnostics.AddRange(result.Diagnostics.Select(ToDiagnostic));
        return response;
    }

    private async Task ValidateAuditedDatabaseAsync(string database)
    {
        try
        {
            _ = ActorStoragePath.NormalizeDatabaseName(database);
        }
        catch (ArgumentException exception)
        {
            await AuditRecorder.OutcomeAsync(grains, "invalid", nameof(StatusCode.InvalidArgument))
                .ConfigureAwait(false);
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
    }

    private static async Task<T> CallAsync<T>(
        Func<GrainCancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GrainCall.RunAsync(call, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception exception) when (exception is not RpcException and not OperationCanceledException)
        {
            Exception root = exception.GetBaseException();
            throw root switch
            {
                ArgumentException => ProtocolMapper.InvalidArgument(root.Message),
                DirectoryNotFoundException => ProtocolMapper.NotFound(root.Message),
                InvalidDataException => ProtocolMapper.DataLoss(root.Message),
                IOException => ProtocolMapper.Unavailable(root.Message),
                UnauthorizedAccessException => ProtocolMapper.Unavailable(root.Message),
                _ => exception
            };
        }
    }

    private static void ThrowIfError(CatalogErrorMessage? error)
    {
        if (error is null || error.Kind == CatalogErrorKind.None)
        {
            return;
        }

        throw error.Kind switch
        {
            CatalogErrorKind.InvalidArgument => ProtocolMapper.InvalidArgument(error.Message),
            _ => ProtocolMapper.Internal(error.Message)
        };
    }

    private static ActorSchemaKind ToActorKind(SchemaKind kind)
    {
        return kind switch
        {
            SchemaKind.Event => ActorSchemaKind.Event,
            SchemaKind.Command => ActorSchemaKind.Command,
            _ => throw ProtocolMapper.InvalidArgument("A schema kind is required.")
        };
    }

    private static ActorSchemaKind ToActorListKind(SchemaKind kind)
    {
        return kind switch
        {
            SchemaKind.Unspecified => ActorSchemaKind.Unspecified,
            SchemaKind.Event => ActorSchemaKind.Event,
            SchemaKind.Command => ActorSchemaKind.Command,
            _ => throw ProtocolMapper.InvalidArgument("The schema kind is invalid.")
        };
    }

    private static SchemaDescription ToSchema(SchemaRegistrationMessage value)
    {
        return new SchemaDescription
        {
            SchemaName = value.Name,
            SchemaKind = ToProtocolKind(value.Kind),
            Fingerprint = value.Fingerprint,
            SchemaDocumentJson = ByteString.CopyFromUtf8(value.DocumentJson)
        };
    }

    private static SchemaSummary ToSchemaSummary(SchemaRegistrationMessage value)
    {
        return new SchemaSummary
        {
            SchemaName = value.Name,
            SchemaKind = ToProtocolKind(value.Kind),
            Fingerprint = value.Fingerprint
        };
    }

    private static SchemaKind ToProtocolKind(ActorSchemaKind kind)
    {
        return kind switch
        {
            ActorSchemaKind.Event => SchemaKind.Event,
            ActorSchemaKind.Command => SchemaKind.Command,
            _ => SchemaKind.Unspecified
        };
    }

    private static HandlerDescription ToHandler(HandlerDescriptionMessage value)
    {
        HandlerDescription result = new()
        {
            HandlerName = value.Name,
            CommandType = value.CommandType,
            NdlSource = value.NdlSource,
            SourceFingerprint = value.SourceFingerprint,
            PlanFingerprint = value.PlanFingerprint,
            Valid = value.Valid
        };
        result.Diagnostics.AddRange(value.Diagnostics.Select(ToDiagnostic));
        if (value.PlanJson is not null)
        {
            result.PlanJson = ByteString.CopyFromUtf8(value.PlanJson);
        }

        if (value.GeneratedNdl is not null)
        {
            result.GeneratedNdl = value.GeneratedNdl;
        }

        result.NdlGenerationDiagnostics.AddRange(value.NdlGenerationDiagnostics.Select(ToDiagnostic));
        return result;
    }

    private static HandlerSummary ToHandlerSummary(HandlerSummaryMessage value)
    {
        return new HandlerSummary
        {
            HandlerName = value.Name,
            CommandType = value.CommandType,
            SourceFingerprint = value.SourceFingerprint,
            PlanFingerprint = value.PlanFingerprint,
            Valid = value.Valid
        };
    }

    private static Diagnostic ToDiagnostic(ActorDiagnosticMessage value)
    {
        Diagnostic result = new()
        {
            Code = value.Code,
            Severity = value.Severity switch
            {
                ActorDiagnosticSeverity.Info => ProtocolDiagnosticSeverity.Info,
                ActorDiagnosticSeverity.Warning => ProtocolDiagnosticSeverity.Warning,
                ActorDiagnosticSeverity.Error => ProtocolDiagnosticSeverity.Error,
                _ => ProtocolDiagnosticSeverity.Unspecified
            },
            Message = value.Message
        };
        if (value.SourceSpan is not null)
        {
            result.SourceSpan = new SourceSpan
            {
                Start = ToPosition(value.SourceSpan.Start),
                End = ToPosition(value.SourceSpan.End)
            };
        }

        return result;
    }

    private static SourcePosition ToPosition(ActorSourcePositionMessage value)
    {
        return new SourcePosition
        {
            Line = checked(value.Line + 1),
            Column = checked(value.Column + 1),
            Offset = value.Offset
        };
    }
}