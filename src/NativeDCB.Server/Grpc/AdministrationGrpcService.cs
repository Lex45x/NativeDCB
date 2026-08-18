using Grpc.Core;

using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Actors.Messages;
using NativeDCB.Engine.Storage.State;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Databases;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

namespace NativeDCB.Server.Grpc;

public sealed class AdministrationGrpcService(DatabaseRegistry registry, IGrainFactory grains)
    : AdministrationService.AdministrationServiceBase
{
    public override async Task<ListPartitionsResponse> ListPartitions(
        ListPartitionsRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        PartitionStatusMessage[] partitions = await ListPartitionsAsync(
            database.Name, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        ListPartitionsResponse response = new();
        foreach (PartitionStatusMessage partition in partitions)
        {
            StateFileInspection inspection = await StateFileInspector.InspectAsync(
                    database.Directory, partition.PartitionNumber, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            response.Partitions.Add(ProtocolMapper.ToPartitionStatus(
                partition, ProtocolMapper.ToStateFileStatus(inspection)));
        }

        return response;
    }

    public override async Task<ListIndexesResponse> ListIndexes(
        ListIndexesRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(
            request.Database, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database.Name);
        WriterStateMessage state = await GrainCall.RunAsync(
            writer.GetStateAsync,
            context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        IIndexCoordinatorGrain coordinator = grains.GetGrain<IIndexCoordinatorGrain>(database.Name);
        IndexListMessage indexes = await GrainCall.RunAsync(
            token => coordinator.ListAsync(state.Head, token),
            context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        ListIndexesResponse response = new();
        response.Indexes.AddRange(indexes.Indexes.Select(ProtocolMapper.ToIndexStatus));
        return response;
    }

    public override async Task<GetStateFileStatusResponse> GetStateFileStatus(
        GetStateFileStatusRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        PartitionStatusMessage[] partitions = await ListPartitionsAsync(
            database.Name, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        PartitionStatusMessage? partition =
            partitions.FirstOrDefault(x => x.PartitionNumber == request.PartitionNumber);
        if (partition is null)
        {
            throw ProtocolMapper.NotFound($"Partition '{request.PartitionNumber}' was not found.");
        }

        StateFileInspection inspection = await StateFileInspector.InspectAsync(
                database.Directory, partition.PartitionNumber, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return new GetStateFileStatusResponse { Status = ProtocolMapper.ToStateFileStatus(inspection) };
    }

    public override async Task<RebuildResponse> RequestIndexRebuild(
        RequestIndexRebuildRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(
            request.Database, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (string.IsNullOrWhiteSpace(request.EventType) || request.Keys.Count == 0)
        {
            throw ProtocolMapper.InvalidArgument("An event type and at least one key are required.");
        }

        EventKeyMessage[] keys = request.Keys
            .Select(ProtocolMapper.ToEventKey)
            .Select(key => new EventKeyMessage(key.Name, key.Value))
            .Distinct()
            .ToArray();
        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database.Name);
        WriterStateMessage state = await GrainCall.RunAsync(
            writer.GetStateAsync,
            context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        IIndexCoordinatorGrain coordinator = grains.GetGrain<IIndexCoordinatorGrain>(database.Name);
        await coordinator.RebuildAsync(request.EventType, keys, state.Head)
            .WaitAsync(context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return new RebuildResponse
        {
            Accepted = true, Message = $"Index rebuild queued for {request.EventType} and {keys.Length} key(s)."
        };
    }

    public override async Task<RebuildResponse> RequestStateRebuild(
        RequestStateRebuildRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(
            request.Database, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (request.PartitionNumber == 0)
        {
            throw ProtocolMapper.InvalidArgument("A positive partition number is required.");
        }

        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database.Name);
        WriterStateMessage state = await GrainCall.RunAsync(
            writer.GetStateAsync,
            context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (request.PartitionNumber >= state.ActivePartition)
        {
            throw ProtocolMapper.FailedPrecondition("State files can only be built for closed partitions.");
        }

        IStateBuilderGrain stateBuilder = grains.GetGrain<IStateBuilderGrain>(database.Name);
        await stateBuilder.RebuildAsync(checked((int)request.PartitionNumber))
            .WaitAsync(context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return new RebuildResponse
        {
            Accepted = true, Message = $"State rebuild queued for partition {request.PartitionNumber}."
        };
    }

    private async Task<PartitionStatusMessage[]> ListPartitionsAsync(
        string database,
        CancellationToken cancellationToken)
    {
        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database);
        WriterStateMessage state = await GrainCall.RunAsync(
            writer.GetStateAsync,
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        IReadGrain reader = grains.GetGrain<IReadGrain>(database);
        PartitionListMessage result = await GrainCall.RunAsync(
            token => reader.ListPartitionsAsync(state.Head, token),
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return result.Partitions;
    }

    private async Task<DatabaseEntry> GetDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.GetAsync(name, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            throw ProtocolMapper.NotFound(exception.Message);
        }
    }
}