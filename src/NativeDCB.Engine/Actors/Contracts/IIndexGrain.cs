using NativeDCB.Engine.Actors.Messages;

namespace NativeDCB.Engine.Actors.Contracts;

public interface IIndexGrain : IGrainWithStringKey
{
    Task ObserveAsync(EventListMessage events, long head);

    Task RebuildAsync(long throughEventIdInclusive);

    Task<IndexStatusMessage> GetStatusAsync(long mainHead, GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadAsync(GrainCancellationToken cancellationToken);
}