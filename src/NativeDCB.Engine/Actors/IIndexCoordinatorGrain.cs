using Orleans.Concurrency;

namespace NativeDCB.Engine.Actors;

public interface IIndexCoordinatorGrain : IGrainWithStringKey
{
    [OneWay]
    Task ObserveAsync(EventListMessage events, long head);

    Task<IndexListMessage> ListAsync(long mainHead, GrainCancellationToken cancellationToken);

    Task<IndexReadResultMessage> ReadAsync(
        EventQueryMessage query,
        GrainCancellationToken cancellationToken);

    [OneWay]
    Task RebuildAsync(string eventType, EventKeyMessage[] keys, long mainHead);
}