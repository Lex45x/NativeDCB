using Orleans.Concurrency;

namespace NativeDCB.Actors.Contracts;

public interface IStateBuilderGrain : IGrainWithStringKey
{
    [OneWay]
    Task BuildMissingAsync(int activePartition);

    [OneWay]
    Task RebuildAsync(int partitionNumber);
}