using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Decisions.Remote;
using NativeDCB.Actors.Messages;

using Orleans.Concurrency;

namespace NativeDCB.Actors.Grains;

[StatelessWorker]
// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class RemoteDecisionRouterGrain(
    IGrainFactory grains,
    RemoteDecisionTokenProtector remoteDecisions) : Grain, IRemoteDecisionRouterGrain
{
    public async Task<CompleteDecisionResultMessage> CompleteAsync(
        RouteRemoteDecisionMessage request,
        GrainCancellationToken cancellationToken)
    {
        if (!remoteDecisions.IsConfigured)
        {
            return Failure(
                DecisionCallErrorKind.FailedPrecondition,
                "RemoteDecisionsNotConfigured",
                remoteDecisions.ConfigurationError ?? "Remote decisions are not configured.");
        }

        if (!remoteDecisions.TryUnprotect(request.ModelSignature, out RemoteDecisionClaimsMessage? claims) ||
            claims is null ||
            !string.Equals(request.Database, claims.Database, StringComparison.Ordinal))
        {
            return Failure(
                DecisionCallErrorKind.InvalidArgument,
                "InvalidSignature",
                "model_signature is invalid.");
        }

        IDecisionGrain decision = grains.GetGrain<IDecisionGrain>(
            $"{claims.Database}|{claims.CommandId:D}");
        return await decision.CompleteAsync(
                new CompleteDecisionMessage(claims, request.ProposedEvents), cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    private static CompleteDecisionResultMessage Failure(
        DecisionCallErrorKind kind,
        string code,
        string message)
    {
        return new CompleteDecisionResultMessage(
            CompleteDecisionOutcome.Failed,
            Guid.Empty,
            string.Empty,
            [],
            Code: code,
            Message: message,
            ErrorKind: kind);
    }
}