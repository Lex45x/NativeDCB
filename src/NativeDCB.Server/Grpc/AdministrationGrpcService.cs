using Grpc.Core;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

namespace NativeDCB.Server.Grpc;

public sealed class AdministrationGrpcService(IGrainFactory grains)
    : AdministrationService.AdministrationServiceBase
{
    public override async Task<ListPartitionsResponse> ListPartitions(
        ListPartitionsRequest request,
        ServerCallContext context)
    {
        IStateOrchestratorGrain orchestrator = StateOrchestrator(request.Database);
        PartitionStateListMessage result = await GrainCall.RunAsync(
                orchestrator.ListPartitionsAsync, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        ListPartitionsResponse response = new();
        response.Partitions.AddRange(result.Partitions.Select(partition => ProtocolMapper.ToPartitionStatus(
            partition.Partition, ProtocolMapper.ToStateFileStatus(partition.StateFile))));
        return response;
    }

    public override async Task<ListIndexesResponse> ListIndexes(
        ListIndexesRequest request,
        ServerCallContext context)
    {
        IIndexOrchestratorGrain orchestrator = IndexOrchestrator(request.Database);
        IndexListResultMessage result = await GrainCall.RunAsync(
                orchestrator.ListIndexesAsync, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        ListIndexesResponse response = new();
        response.Indexes.AddRange(result.Indexes.Select(ProtocolMapper.ToIndexStatus));
        return response;
    }

    public override async Task<GetStateFileStatusResponse> GetStateFileStatus(
        GetStateFileStatusRequest request,
        ServerCallContext context)
    {
        IStateOrchestratorGrain orchestrator = StateOrchestrator(request.Database);
        StateFileStatusResultMessage result = await GrainCall.RunAsync(
                token => orchestrator.GetStateFileStatusAsync(request.PartitionNumber, token), context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new GetStateFileStatusResponse { Status = ProtocolMapper.ToStateFileStatus(result.Status!) };
    }

    public override async Task<RebuildResponse> RequestIndexRebuild(
        RequestIndexRebuildRequest request,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.EventType) || request.Keys.Count == 0)
        {
            throw ProtocolMapper.InvalidArgument("An event type and at least one key are required.");
        }

        EventKeyMessage[] keys = request.Keys
            .Select(ProtocolMapper.ToEventKey)
            .Select(key => new EventKeyMessage(key.Name, key.Value))
            .Distinct()
            .ToArray();
        IIndexOrchestratorGrain orchestrator = IndexOrchestrator(request.Database);
        AdministrationOperationResultMessage result = await GrainCall.RunAsync(
                token => orchestrator.RequestIndexRebuildAsync(request.EventType, keys, token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new RebuildResponse
        {
            Accepted = true,
            Message = $"Index rebuild queued for {request.EventType} and {keys.Length} key(s)."
        };
    }

    public override async Task<RebuildResponse> RequestStateRebuild(
        RequestStateRebuildRequest request,
        ServerCallContext context)
    {
        ValidateRebuildPartitionNumber(request.PartitionNumber);
        IStateOrchestratorGrain orchestrator = StateOrchestrator(request.Database);
        AdministrationOperationResultMessage result = await GrainCall.RunAsync(
                token => orchestrator.RequestStateRebuildAsync(request.PartitionNumber, token), context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        ThrowIfError(result.Error);
        return new RebuildResponse
        {
            Accepted = true,
            Message = $"State rebuild queued for partition {request.PartitionNumber}."
        };
    }

    private static void ValidateRebuildPartitionNumber(uint partitionNumber)
    {
        if (partitionNumber == 0)
        {
            throw ProtocolMapper.InvalidArgument("A positive partition number is required.");
        }
    }

    private IStateOrchestratorGrain StateOrchestrator(string database)
    {
        ValidateDatabase(database);
        return grains.GetGrain<IStateOrchestratorGrain>(database);
    }

    private IIndexOrchestratorGrain IndexOrchestrator(string database)
    {
        ValidateDatabase(database);
        return grains.GetGrain<IIndexOrchestratorGrain>(database);
    }

    private static void ValidateDatabase(string database)
    {
        if (string.IsNullOrWhiteSpace(database) || Path.IsPathRooted(database))
        {
            throw ProtocolMapper.InvalidArgument("A relative database name is required.");
        }

        string normalized = database.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw ProtocolMapper.InvalidArgument("The database name contains an invalid path segment.");
        }
    }

    private static void ThrowIfError(AdministrationErrorMessage? error)
    {
        if (error is null)
        {
            return;
        }

        throw error.Kind switch
        {
            AdministrationErrorKind.InvalidArgument => ProtocolMapper.InvalidArgument(error.Message),
            AdministrationErrorKind.NotFound => ProtocolMapper.NotFound(error.Message),
            AdministrationErrorKind.FailedPrecondition => ProtocolMapper.FailedPrecondition(error.Message),
            AdministrationErrorKind.Unavailable => ProtocolMapper.Unavailable(error.Message),
            AdministrationErrorKind.DataLoss => ProtocolMapper.DataLoss(error.Message),
            _ => ProtocolMapper.Internal(error.Message)
        };
    }
}