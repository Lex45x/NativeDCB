using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IIndexReplicaGrain : IGrainWithStringKey
{
    Task<IndexSnapshotMessage> ReadSnapshotAsync(GrainCancellationToken cancellationToken);
}