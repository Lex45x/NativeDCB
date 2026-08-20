using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class StateOrchestratorGrain(ActorStoragePath storage, IGrainFactory grains)
    : Grain, IStateOrchestratorGrain
{
    public async Task<PartitionStateListMessage> ListPartitionsAsync(
        GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        if (ValidateDatabase(database) is { } error)
        {
            return new PartitionStateListMessage([], error);
        }

        PartitionStatusMessage[] partitions = await ListPartitionStatusesAsync(database, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        Task<StateFileStatusMessage>[] inspections = partitions.Select(partition =>
            State(database, partition.PartitionNumber).InspectAsync(cancellationToken)).ToArray();
        StateFileStatusMessage[] statuses = await Task.WhenAll(inspections)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new PartitionStateListMessage(partitions.Zip(statuses)
            .Select(pair => new PartitionStateStatusMessage(pair.First, pair.Second))
            .ToArray());
    }

    public async Task<StateFileStatusResultMessage> GetStateFileStatusAsync(
        uint partitionNumber,
        GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        if (ValidateDatabase(database) is { } error)
        {
            return new StateFileStatusResultMessage(null, error);
        }

        PartitionStatusMessage[] partitions = await ListPartitionStatusesAsync(database, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        PartitionStatusMessage? partition = partitions.FirstOrDefault(
            value => checked((uint)value.PartitionNumber) == partitionNumber);
        if (partition is null)
        {
            return new StateFileStatusResultMessage(null, Error(
                AdministrationErrorKind.NotFound, $"Partition '{partitionNumber}' was not found."));
        }

        StateFileStatusMessage status = await State(database, partition.PartitionNumber).InspectAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new StateFileStatusResultMessage(status);
    }

    public async Task<AdministrationOperationResultMessage> RequestStateRebuildAsync(
        uint partitionNumber,
        GrainCancellationToken cancellationToken)
    {
        string database = this.GetPrimaryKeyString();
        if (ValidateDatabase(database) is { } error)
        {
            return new AdministrationOperationResultMessage(error);
        }

        WriterStateMessage state = await grains.GetGrain<IMainWriterGrain>(database)
            .GetStateAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        if (partitionNumber >= checked((uint)state.ActivePartition))
        {
            return new AdministrationOperationResultMessage(Error(
                AdministrationErrorKind.FailedPrecondition,
                "State files can only be built for closed partitions."));
        }

        await State(database, checked((int)partitionNumber)).RequestRebuildAsync()
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new AdministrationOperationResultMessage();
    }

    private async Task<PartitionStatusMessage[]> ListPartitionStatusesAsync(
        string database,
        GrainCancellationToken cancellationToken)
    {
        WriterStateMessage state = await grains.GetGrain<IMainWriterGrain>(database)
            .GetStateAsync(cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        PartitionListMessage result = await grains.GetGrain<IReadGrain>(database)
            .ListPartitionsAsync(state.Head, cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return result.Partitions;
    }

    private IStateGrain State(string database, int partitionNumber)
    {
        return grains.GetGrain<IStateGrain>(StateGrainKey.Create(database, partitionNumber));
    }

    private AdministrationErrorMessage? ValidateDatabase(string database)
    {
        try
        {
            _ = storage.GetDatabaseDirectory(database);
            return null;
        }
        catch (ArgumentException exception)
        {
            return Error(AdministrationErrorKind.InvalidArgument, exception.Message);
        }
        catch (DirectoryNotFoundException exception)
        {
            return Error(AdministrationErrorKind.NotFound, exception.Message);
        }
    }

    private static AdministrationErrorMessage Error(AdministrationErrorKind kind, string message)
    {
        return new AdministrationErrorMessage(kind, message);
    }
}