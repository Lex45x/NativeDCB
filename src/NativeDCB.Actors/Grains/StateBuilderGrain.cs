using NativeDCB.Actors.Contracts;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class StateBuilderGrain(IGrainFactory grains) : Grain, IStateBuilderGrain
{
    public async Task BuildMissingAsync(int activePartition)
    {
        if (activePartition <= 1)
        {
            return;
        }

        string database = this.GetPrimaryKeyString();
        for (int partition = 1; partition < activePartition; partition++)
        {
            await grains.GetGrain<IStateGrain>(StateGrainKey.Create(database, partition)).RebuildAsync()
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public async Task RebuildAsync(int partitionNumber)
    {
        await grains.GetGrain<IStateGrain>(StateGrainKey.Create(this.GetPrimaryKeyString(), partitionNumber))
            .RequestRebuildAsync()
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }
}