namespace NativeDCB.Engine.Actors;

public sealed class StateBuilderGrain(IDatabaseStoreProvider stores) : Grain, IStateBuilderGrain
{
    public async Task BuildMissingAsync(int activePartition)
    {
        if (activePartition <= 1)
        {
            return;
        }

        string directory = stores.GetDirectory(this.GetPrimaryKeyString());
        StateBuilder builder = new(new DatabaseOptions(directory));
        for (int partition = 1; partition < activePartition; partition++)
        {
            await builder.BuildAsync(partition)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    public async Task RebuildAsync(int partitionNumber)
    {
        string directory = stores.GetDirectory(this.GetPrimaryKeyString());
        StateBuilder builder = new(new DatabaseOptions(directory));
        await builder.BuildAsync(partitionNumber)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }
}