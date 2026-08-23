using System.Diagnostics;
using System.Security.Claims;

using Grpc.Core;
using Grpc.Core.Interceptors;

using Microsoft.AspNetCore.Authentication;

using NativeDCB.Actors.Audit;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Decisions.Remote;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;
using NativeDCB.Server.Observability;

using NdlParser = NativeDCB.Ndl.Ndl;

namespace NativeDCB.Server.Security;

internal sealed class GrpcAuthorizationInterceptor(
    RemoteDecisionTokenProtector remoteDecisions,
    IGrainFactory grains,
    ILogger<GrpcAuthorizationInterceptor> logger) : Interceptor
{
    private static readonly HashSet<string> AuditedOperations = new(StringComparer.Ordinal)
    {
        "/nativedcb.v1.DatabaseService/CreateDatabase",
        "/nativedcb.v1.CatalogService/RegisterEventSchema",
        "/nativedcb.v1.CatalogService/RegisterCommandSchema",
        "/nativedcb.v1.CatalogService/RemoveSchema",
        "/nativedcb.v1.CatalogService/RegisterHandler",
        "/nativedcb.v1.CatalogService/RemoveHandler",
        "/nativedcb.v1.CommandService/ExecuteHandler",
        "/nativedcb.v1.CommandService/PrepareDecision",
        "/nativedcb.v1.CommandService/CompleteDecision",
        "/nativedcb.v1.StatementService/ExecuteStatement",
        "/nativedcb.v1.AdministrationService/RequestIndexRebuild",
        "/nativedcb.v1.AdministrationService/RequestStateRebuild",
        "/nativedcb.v1.AuthenticationService/CreateApiKey",
        "/nativedcb.v1.AuthenticationService/RevokeApiKey"
    };

    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        AuditOperationContextMessage? audit = await AuthorizeAndAuditAsync(request, context).ConfigureAwait(false);
        object? previous = audit is null ? null : AuditRequestContext.Set(audit);
        try
        {
            return await continuation(request, context).ConfigureAwait(false);
        }
        finally
        {
            if (audit is not null)
            {
                AuditRequestContext.Restore(previous);
            }
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        AuditOperationContextMessage? audit = await AuthorizeAndAuditAsync(request, context).ConfigureAwait(false);
        object? previous = audit is null ? null : AuditRequestContext.Set(audit);
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(false);
        }
        finally
        {
            if (audit is not null)
            {
                AuditRequestContext.Restore(previous);
            }
        }
    }

    public override async Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAndAuditAsync<object?>(request: null, context).ConfigureAwait(false);
        return await continuation(requestStream, context).ConfigureAwait(false);
    }

    public override async Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        await AuthorizeAndAuditAsync<object?>(request: null, context).ConfigureAwait(false);
        await continuation(requestStream, responseStream, context).ConfigureAwait(false);
    }

    private async Task<AuditOperationContextMessage?> AuthorizeAndAuditAsync<TRequest>(
        TRequest request,
        ServerCallContext context)
    {
        try
        {
            Authorize(request, context);
        }
        catch (RpcException exception) when
            (exception.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            await TryRecordDenialAsync(request, context, exception).ConfigureAwait(false);
            throw;
        }

        if (!AuditedOperations.Contains(context.Method))
        {
            return null;
        }

        AuditOperationContextMessage audit;
        try
        {
            audit = CreateContext(request, context);
        }
        catch (RpcException exception) when
            (exception.StatusCode is StatusCode.Unauthenticated or StatusCode.PermissionDenied)
        {
            await TryRecordDenialAsync(request, context, exception).ConfigureAwait(false);
            throw;
        }

        try
        {
            await AppendAsync(new AppendAuditRecordMessage(
                    audit.OperationId,
                    "attempt",
                    audit.Category,
                    audit.Operation,
                    audit.AuthenticationScheme,
                    audit.Subject,
                    audit.Issuer,
                    audit.Database,
                    audit.Resource,
                    TraceId: audit.TraceId,
                    CommandId: audit.CommandId),
                context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not RpcException and not OperationCanceledException)
        {
            ServerTelemetry.AuditFailures.Add(1,
                new KeyValuePair<string, object?>("operation", context.Method),
                new KeyValuePair<string, object?>("fault.kind", "attempt_persistence"));
            logger.LogError(exception, "Audit persistence rejected operation {Operation} before mutation.",
                context.Method);
            throw ProtocolMapper.Unavailable("The audit journal is unavailable.");
        }

        return audit;
    }

    private void Authorize<TRequest>(TRequest request, ServerCallContext context)
    {
        AuthorizeAction(context);

        PermissionGrant? resource;
        try
        {
            resource = (context.Method, request) switch
            {
                ("/nativedcb.v1.CatalogService/RegisterHandler", RegisterHandlerRequest value) =>
                    PermissionEvaluator.Handler(value.Database, value.HandlerName, "register"),
                ("/nativedcb.v1.CatalogService/RemoveHandler", RemoveHandlerRequest value) =>
                    PermissionEvaluator.Handler(value.Database, value.HandlerName, "remove"),
                ("/nativedcb.v1.CatalogService/GetHandler", GetHandlerRequest value) =>
                    PermissionEvaluator.Handler(value.Database, value.HandlerName, "read"),
                ("/nativedcb.v1.CatalogService/ListHandlers", ListHandlersRequest value) =>
                    PermissionEvaluator.HandlerList(value.Database),
                ("/nativedcb.v1.CommandService/ExecuteHandler", ExecuteHandlerRequest value) =>
                    PermissionEvaluator.Handler(value.Database, value.HandlerName, "execute"),
                ("/nativedcb.v1.CommandService/PrepareDecision", PrepareDecisionRequest value) =>
                    PermissionEvaluator.Handler(value.Database, value.HandlerName, "prepare"),
                ("/nativedcb.v1.AuthenticationService/CreateApiKey", CreateApiKeyRequest) =>
                    PermissionEvaluator.ApiKey("*", "create"),
                ("/nativedcb.v1.AuthenticationService/ListApiKeys", ListApiKeysRequest) =>
                    PermissionEvaluator.ApiKey("*", "list"),
                ("/nativedcb.v1.AuthenticationService/RevokeApiKey", RevokeApiKeyRequest value) =>
                    PermissionEvaluator.ApiKey(value.KeyId, "revoke"),
                ("/nativedcb.v1.AuditService/ListAuditRecords", ListAuditRecordsRequest value) =>
                    PermissionEvaluator.Audit(value.HasDatabase ? value.Database : "*"),
                _ => null
            };
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }

        if (resource is not null)
        {
            Require(context, resource.Value);
        }

        if (context.Method == "/nativedcb.v1.CommandService/CompleteDecision" &&
            request is CompleteDecisionRequest complete)
        {
            if (!remoteDecisions.IsConfigured)
            {
                throw ProtocolMapper.FailedPrecondition(
                    remoteDecisions.ConfigurationError ?? "Remote decisions are not configured.");
            }

            if (!remoteDecisions.TryGetAuthorizationResource(
                    complete.ModelSignature.Span, out string database, out string handler) ||
                !string.Equals(complete.Database, database, StringComparison.Ordinal))
            {
                throw ProtocolMapper.InvalidArgument("model_signature is invalid.");
            }

            Require(context, PermissionEvaluator.Handler(database, handler, "complete"));
        }

        if (context.Method == "/nativedcb.v1.StatementService/ExecuteStatement" &&
            request is ExecuteStatementRequest statement)
        {
            ParseResult parsed = NdlParser.Parse(statement.NdlSource);
            if (!parsed.HasErrors)
            {
                foreach (string handlerName in parsed.Document.Decisions.Select(decision => decision.Name))
                {
                    Require(context, PermissionEvaluator.Handler(statement.Database, handlerName, "register"));
                }
            }
        }
    }

    private static void AuthorizeAction(ServerCallContext context)
    {
        if (context.GetHttpContext().User.Identity?.IsAuthenticated != true)
        {
            throw ProtocolMapper.Unauthenticated("Caller authentication is required.");
        }

        Require(context, PermissionEvaluator.Grpc(context.Method));
    }

    private static void Require(ServerCallContext context, PermissionGrant required)
    {
        if (!PermissionEvaluator.HasPermission(context.GetHttpContext().User, required))
        {
            throw ProtocolMapper.PermissionDenied("The caller does not have the required permission.");
        }
    }

    private AuditOperationContextMessage CreateContext<TRequest>(TRequest request, ServerCallContext context)
    {
        ClaimsPrincipal principal = context.GetHttpContext().User;
        string? scheme = AuthenticationScheme(context);
        string? subject = scheme == AuthenticationProviderNames.JwtBearer
            ? principal.FindFirstValue("sub")
            : principal.FindFirstValue(ClaimTypes.NameIdentifier);
        string? issuer = scheme == AuthenticationProviderNames.JwtBearer
            ? principal.FindFirstValue("iss")
            : null;
        if (scheme == AuthenticationProviderNames.JwtBearer && string.IsNullOrWhiteSpace(subject))
        {
            throw ProtocolMapper.PermissionDenied("Audited JWT operations require a non-empty sub claim.");
        }

        (string category, string? database, string? resource, string? commandId) = Describe(request, context.Method);
        if (request is CompleteDecisionRequest complete && remoteDecisions.TryGetAuthorizationResource(
                complete.ModelSignature.Span, out string signedDatabase, out string signedHandler))
        {
            database = NormalizeDatabase(signedDatabase);
            resource = $"handler:{PermissionEvaluator.Handler(signedDatabase, signedHandler, "complete").Resource}";
        }

        return new AuditOperationContextMessage(
            Guid.NewGuid(),
            category,
            context.Method,
            scheme,
            subject,
            issuer,
            database,
            resource,
            Activity.Current?.TraceId.ToString(),
            commandId);
    }

    private async Task TryRecordDenialAsync<TRequest>(
        TRequest request,
        ServerCallContext context,
        RpcException denial)
    {
        ServerTelemetry.AuthorizationDenials.Add(1,
            new KeyValuePair<string, object?>("operation", context.Method),
            new KeyValuePair<string, object?>("status", denial.StatusCode.ToString()));
        try
        {
            ClaimsPrincipal principal = context.GetHttpContext().User;
            string? scheme = AuthenticationScheme(context);
            var (_, database, resource, commandId) = Describe(request, context.Method);
            await AppendAsync(new AppendAuditRecordMessage(
                    Guid.NewGuid(),
                    "denied",
                    "authorization",
                    context.Method,
                    scheme,
                    scheme == AuthenticationProviderNames.JwtBearer
                        ? principal.FindFirstValue("sub")
                        : principal.FindFirstValue(ClaimTypes.NameIdentifier),
                    scheme == AuthenticationProviderNames.JwtBearer ? principal.FindFirstValue("iss") : null,
                    database,
                    resource,
                    denial.StatusCode == StatusCode.Unauthenticated ? "unauthenticated" : "permission_denied",
                    GrpcStatus: denial.StatusCode.ToString(),
                    TraceId: Activity.Current?.TraceId.ToString(),
                    CommandId: commandId),
                CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            ServerTelemetry.AuditFailures.Add(1,
                new KeyValuePair<string, object?>("operation", context.Method),
                new KeyValuePair<string, object?>("fault.kind", "denial_persistence"));
            logger.LogError(exception, "Could not persist denial audit record for {Operation}.", context.Method);
        }
    }

    private async Task AppendAsync(AppendAuditRecordMessage record, CancellationToken cancellationToken)
    {
        await GrainCall.RunAsync(
                token => grains.GetGrain<IAuditGrain>(IAuditGrain.SingletonKey).AppendAsync(record, token),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static string? AuthenticationScheme(ServerCallContext context)
    {
        return context.GetHttpContext().Features.Get<IAuthenticateResultFeature>()
                   ?.AuthenticateResult?.Ticket?.AuthenticationScheme
               ?? context.GetHttpContext().User.Identity?.AuthenticationType;
    }

    private static (string Category, string? Database, string? Resource, string? CommandId) Describe<TRequest>(
        TRequest request,
        string method)
    {
        string category = method.Contains("CommandService", StringComparison.Ordinal) ? "decision" :
            method.Contains("CatalogService", StringComparison.Ordinal) ||
            method.Contains("StatementService", StringComparison.Ordinal) ? "catalog" :
            method.Contains("AdministrationService", StringComparison.Ordinal) ? "administration" :
            method.Contains("AuthenticationService", StringComparison.Ordinal) ? "security" :
            method.Contains("AuditService", StringComparison.Ordinal) ? "audit" : "database";

        return request switch
        {
            CreateDatabaseRequest value => (category, NormalizeDatabase(value.Database), null, null),
            RegisterSchemaRequest value => (category, NormalizeDatabase(value.Database), $"schema:{value.SchemaName}", null),
            RemoveSchemaRequest value => (category, NormalizeDatabase(value.Database), $"schema:{value.SchemaName}", null),
            RegisterHandlerRequest value => (category, NormalizeDatabase(value.Database),
                HandlerResource(value.Database, value.HandlerName, "register"), null),
            RemoveHandlerRequest value => (category, NormalizeDatabase(value.Database),
                HandlerResource(value.Database, value.HandlerName, "remove"), null),
            ExecuteHandlerRequest value => (category, NormalizeDatabase(value.Database),
                HandlerResource(value.Database, value.HandlerName, "execute"),
                value.HasCommandId ? NormalizeCommandId(value.CommandId) : null),
            PrepareDecisionRequest value => (category, NormalizeDatabase(value.Database),
                HandlerResource(value.Database, value.HandlerName, "prepare"),
                value.HasCommandId ? NormalizeCommandId(value.CommandId) : null),
            CompleteDecisionRequest value => (category, NormalizeDatabase(value.Database), null, null),
            ExecuteStatementRequest value => (category, NormalizeDatabase(value.Database), "statement", null),
            RequestIndexRebuildRequest value => (category, NormalizeDatabase(value.Database), $"index:{value.EventType}", null),
            RequestStateRebuildRequest value => (category, NormalizeDatabase(value.Database), $"state:{value.PartitionNumber}", null),
            CreateApiKeyRequest => (category, null, "apikey:new", null),
            RevokeApiKeyRequest value => (category, null, $"apikey:{value.KeyId}", null),
            ListAuditRecordsRequest value => (category,
                value.HasDatabase ? NormalizeDatabase(value.Database) : null, "audit", null),
            _ => (category, null, null, null)
        };
    }

    private static string? HandlerResource(string database, string handler, string action)
    {
        try
        {
            return $"handler:{PermissionEvaluator.Handler(database, handler, action).Resource}";
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? NormalizeDatabase(string database)
    {
        try
        {
            return ActorStoragePath.NormalizeDatabaseName(database);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    private static string? NormalizeCommandId(string commandId)
    {
        return Guid.TryParseExact(commandId, "D", out Guid parsed) && parsed != Guid.Empty
            ? parsed.ToString("D")
            : null;
    }
}