using Grpc.Core;
using Grpc.Core.Interceptors;

using NativeDCB.Engine.Storage.EventLog;

namespace NativeDCB.Server.Grpc.Infrastructure;

public sealed class GrpcExceptionInterceptor(ILogger<GrpcExceptionInterceptor> logger) : Interceptor
{
    public override async Task<TResponse> UnaryServerHandler<TRequest, TResponse>(
        TRequest request,
        ServerCallContext context,
        UnaryServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            return await continuation(request, context).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception exception)
        {
            LogUnexpected(exception);
            throw Map(exception);
        }
    }

    public override async Task ServerStreamingServerHandler<TRequest, TResponse>(
        TRequest request,
        IServerStreamWriter<TResponse> responseStream,
        ServerCallContext context,
        ServerStreamingServerMethod<TRequest, TResponse> continuation)
    {
        try
        {
            await continuation(request, responseStream, context).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception exception)
        {
            LogUnexpected(exception);
            throw Map(exception);
        }
    }

    private static Exception Map(Exception exception)
    {
        if (exception is RpcException or OperationCanceledException)
        {
            return exception;
        }

        Exception root = exception.GetBaseException();
        return root switch
        {
            InvalidDataException => ProtocolMapper.DataLoss(root.Message),
            EventStoreUnavailableException => ProtocolMapper.Unavailable(root.Message),
            IOException => ProtocolMapper.Unavailable(root.Message),
            UnauthorizedAccessException => ProtocolMapper.Unavailable(root.Message),
            _ => ProtocolMapper.Internal("An unexpected server error occurred.")
        };
    }

    private void LogUnexpected(Exception exception)
    {
        if (exception is not RpcException and not OperationCanceledException)
        {
            logger.LogError(exception, "An unhandled gRPC service exception was mapped to a transport status.");
        }
    }
}