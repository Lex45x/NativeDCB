using NativeDCB.Actors.Messages;

using Orleans.Concurrency;

namespace NativeDCB.Actors.Contracts;

public interface IStateGrain : IGrainWithStringKey
{
    Task<StateFileStatusMessage> InspectAsync(GrainCancellationToken cancellationToken);

    Task RebuildAsync();

    [OneWay]
    Task RequestRebuildAsync();
}