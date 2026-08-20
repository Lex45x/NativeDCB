using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IRemoteDecisionRouterGrain : IGrainWithStringKey
{
    Task<CompleteDecisionResultMessage> CompleteAsync(
        RouteRemoteDecisionMessage request,
        GrainCancellationToken cancellationToken);
}