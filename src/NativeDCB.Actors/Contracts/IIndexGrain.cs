using NativeDCB.Actors.Messages;

using Orleans.Concurrency;

namespace NativeDCB.Actors.Contracts;

public interface IIndexGrain : IGrainWithStringKey
{
    [OneWay]
    Task AdvanceAsync(long throughEventIdInclusive);

    Task RebuildAsync(long throughEventIdInclusive, GrainCancellationToken cancellationToken);

    Task<IndexStatusMessage> GetStatusAsync(long mainHead, GrainCancellationToken cancellationToken);

}