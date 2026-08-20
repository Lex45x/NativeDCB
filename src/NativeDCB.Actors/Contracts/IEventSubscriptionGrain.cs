using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IEventSubscriptionGrain : IGrainWithStringKey
{
    Task OpenAsync(
        EventSubscriptionOpenMessage request,
        GrainCancellationToken cancellationToken);

    Task<EventSubscriptionReadResultMessage> ReadNextAsync(
        EventSubscriptionReadNextMessage request,
        GrainCancellationToken cancellationToken);

    Task CloseAsync(
        EventSubscriptionCloseMessage request,
        GrainCancellationToken cancellationToken);
}