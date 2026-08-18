using NativeDCB.Engine.Actors.Messages;

namespace NativeDCB.Engine.Actors.Contracts;

public interface ITransactionGrain : IGrainWithStringKey
{
    Task<DecisionResultMessage> ExecuteAsync(
        TransactionRequestMessage request,
        GrainCancellationToken cancellationToken);
}