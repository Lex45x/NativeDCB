using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IStateOrchestratorGrain : IGrainWithStringKey
{
    Task<PartitionStateListMessage> ListPartitionsAsync(GrainCancellationToken cancellationToken);

    Task<StateFileStatusResultMessage> GetStateFileStatusAsync(
        uint partitionNumber,
        GrainCancellationToken cancellationToken);

    Task<AdministrationOperationResultMessage> RequestStateRebuildAsync(
        uint partitionNumber,
        GrainCancellationToken cancellationToken);
}