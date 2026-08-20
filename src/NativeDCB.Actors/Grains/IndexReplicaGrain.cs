using System.Text.Json;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.Indexes;
using NativeDCB.Model;
using NativeDCB.Model.Events;

using Orleans.Concurrency;

namespace NativeDCB.Actors.Grains;

[StatelessWorker]
// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class IndexReplicaGrain(ActorStoragePath storage) : Grain, IIndexReplicaGrain
{
    public async Task<IndexSnapshotMessage> ReadSnapshotAsync(GrainCancellationToken cancellationToken)
    {
        IndexIdentity identity = IndexIdentity.Decode(this.GetPrimaryKeyString());
        try
        {
            IndexFileModel? model = await IndexFileStore.TryReadPublishedAsync(
                    storage.GetDatabaseDirectory(identity.Database),
                    identity.EventType,
                    new EventKey(identity.Key.Name, identity.Key.Value),
                    cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return new IndexSnapshotMessage(
                Supported: model is not null,
                model?.Head ?? 0,
                (model?.Events ?? []).Select(ActorMessageMapper.ToMessage).ToArray(),
                Fault: null);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or JsonException or
                                                    UnauthorizedAccessException)
        {
            return new IndexSnapshotMessage(Supported: false, Head: 0, Events: [], exception.Message);
        }
    }
}