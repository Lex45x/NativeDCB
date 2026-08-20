using Grpc.Core;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

namespace NativeDCB.Server.Grpc;

public sealed class DatabaseGrpcService(IGrainFactory grains) : DatabaseService.DatabaseServiceBase
{
    public override async Task<ListDatabasesResponse> ListDatabases(
        ListDatabasesRequest request,
        ServerCallContext context)
    {
        ListDatabasesActorResponse result = await RunAsync(
                (grain, token) => grain.ListAsync(new ListDatabasesActorRequest(), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ListDatabasesResponse response = new();
        response.Databases.AddRange(result.Databases.Select(ProtocolMapper.ToDatabaseSummary));
        return response;
    }

    public override async Task<CreateDatabaseResponse> CreateDatabase(
        CreateDatabaseRequest request,
        ServerCallContext context)
    {
        CreateDatabaseActorResponse result = await RunAsync(
                (grain, token) => grain.CreateAsync(new CreateDatabaseActorRequest(request.Database), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new CreateDatabaseResponse
        {
            Database = ProtocolMapper.ToDatabaseInfo(result.Database!)
        };
    }

    public override async Task<GetDatabaseInfoResponse> GetDatabaseInfo(
        GetDatabaseInfoRequest request,
        ServerCallContext context)
    {
        GetDatabaseInfoActorResponse result = await RunAsync(
                (grain, token) => grain.GetInfoAsync(new GetDatabaseInfoActorRequest(request.Database), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new GetDatabaseInfoResponse
        {
            Database = ProtocolMapper.ToDatabaseInfo(result.Database!)
        };
    }

    public override async Task<GetHealthResponse> GetHealth(
        GetHealthRequest request,
        ServerCallContext context)
    {
        GetHealthActorResponse result = await RunAsync(
                (grain, token) => grain.GetHealthAsync(new GetHealthActorRequest(), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        GetHealthResponse response = new() { Live = result.Live };
        response.Databases.AddRange(result.Databases.Select(ProtocolMapper.ToDatabaseHealth));
        return response;
    }

    public override async Task<GetCapabilitiesResponse> GetCapabilities(
        GetCapabilitiesRequest request,
        ServerCallContext context)
    {
        GetCapabilitiesActorResponse result = await RunAsync(
                (grain, token) => grain.GetCapabilitiesAsync(new GetCapabilitiesActorRequest(), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        GetCapabilitiesResponse response = new()
        {
            ProtocolVersion = result.ProtocolVersion,
            NdlVersion = result.NdlVersion,
            SubscriptionsSupported = result.SubscriptionsSupported
        };
        response.FileFormatVersions.AddRange(result.FileFormatVersions);
        response.QueryFeatures.AddRange(result.QueryFeatures);
        foreach (CapabilityLimitMessage limit in result.Limits)
        {
            response.Limits.Add(limit.Name, limit.Value);
        }

        return response;
    }

    public override async Task<GetHeadResponse> GetHead(
        GetHeadRequest request,
        ServerCallContext context)
    {
        GetMainHeadActorResponse result = await RunAsync(
                (grain, token) => grain.GetMainHeadAsync(new GetMainHeadActorRequest(request.Database), token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new GetHeadResponse { EventId = result.EventId };
    }

    private Task<T> RunAsync<T>(
        Func<IDatabaseDirectoryGrain, GrainCancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        IDatabaseDirectoryGrain directory = grains.GetGrain<IDatabaseDirectoryGrain>(
            IDatabaseDirectoryGrain.SingletonKey);
        return GrainCall.RunAsync(token => call(directory, token), cancellationToken);
    }

    private static void ThrowIfError(DatabaseDirectoryErrorMessage? error)
    {
        if (error is null)
        {
            return;
        }

        throw error.Kind switch
        {
            DatabaseDirectoryErrorKind.InvalidArgument => ProtocolMapper.InvalidArgument(error.Message),
            DatabaseDirectoryErrorKind.NotFound => ProtocolMapper.NotFound(error.Message),
            DatabaseDirectoryErrorKind.AlreadyExists => ProtocolMapper.AlreadyExists(error.Message),
            DatabaseDirectoryErrorKind.Unavailable => ProtocolMapper.Unavailable(error.Message),
            DatabaseDirectoryErrorKind.DataLoss => ProtocolMapper.DataLoss(error.Message),
            _ => ProtocolMapper.Internal(error.Message)
        };
    }
}