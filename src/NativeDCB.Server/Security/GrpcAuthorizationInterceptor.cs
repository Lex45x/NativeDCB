using Grpc.Core;
using Grpc.Core.Interceptors;

using NativeDCB.Actors.Decisions.Remote;
using NativeDCB.Ndl.Parsing;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Grpc.Infrastructure;

using NdlParser = NativeDCB.Ndl.Ndl;

namespace NativeDCB.Server.Security;

internal sealed class GrpcAuthorizationInterceptor(RemoteDecisionTokenProtector remoteDecisions) : Interceptor
{
    public override Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(request, context);
        return continuation(request, context);
    }

    public override Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        Authorize(request, context);
        return continuation(request, responseStream, context);
    }

    public override Task<TResponse> ClientStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        ServerCallContext context,
        ClientStreamingServerMethod<TRequest, TResponse> continuation)
    {
        AuthorizeAction(context);
        return continuation(requestStream, context);
    }

    public override Task DuplexStreamingServerHandler<TRequest, TResponse>(
        IAsyncStreamReader<TRequest> requestStream,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        DuplexStreamingServerMethod<TRequest, TResponse> continuation)
    {
        AuthorizeAction(context);
        return continuation(requestStream, responseStream, context);
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
}
