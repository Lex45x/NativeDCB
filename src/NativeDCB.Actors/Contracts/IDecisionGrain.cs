using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IDecisionGrain : IGrainWithStringKey
{
    Task<ExecuteDecisionResultMessage> ExecuteAsync(
        ExecuteDecisionMessage request,
        GrainCancellationToken cancellationToken);

    Task<PrepareDecisionResultMessage> PrepareAsync(
        PrepareDecisionMessage request,
        GrainCancellationToken cancellationToken);

    Task<CompleteDecisionResultMessage> CompleteAsync(
        CompleteDecisionMessage request,
        GrainCancellationToken cancellationToken);
}