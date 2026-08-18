namespace NativeDCB.Engine.Actors;

public interface ITransactionGrain : IGrainWithStringKey
{
    Task<DecisionResultMessage> ExecuteAsync(
        TransactionRequestMessage request,
        GrainCancellationToken cancellationToken);
}