using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IMainWriterGrain : IGrainWithStringKey
{
    Task<WriterStateMessage> CreateAsync(GrainCancellationToken cancellationToken);

    Task<MainStatusMessage> GetStatusAsync(GrainCancellationToken cancellationToken);

    Task<WriterStateMessage> GetStateAsync(GrainCancellationToken cancellationToken);

    Task<StoreIdMessage> GetStoreIdAsync(GrainCancellationToken cancellationToken);

    Task<AppendResultMessage> AppendAsync(
        EventBatchMessage batch,
        AppendConditionMessage condition,
        GrainCancellationToken cancellationToken);
}