using Orleans.Concurrency;

namespace NativeDCB.Engine.Actors;

public interface IStateBuilderGrain : IGrainWithStringKey
{
    [OneWay]
    Task BuildMissingAsync(int activePartition);

    [OneWay]
    Task RebuildAsync(int partitionNumber);
}