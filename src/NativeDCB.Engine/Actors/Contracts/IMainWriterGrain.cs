using NativeDCB.Engine.Actors.Messages;

namespace NativeDCB.Engine.Actors.Contracts;

public interface IMainWriterGrain : IGrainWithStringKey
{
    Task<WriterStateMessage> GetStateAsync(GrainCancellationToken cancellationToken);

    Task<AppendResultMessage> AppendAsync(
        EventBatchMessage batch,
        AppendConditionMessage condition,
        GrainCancellationToken cancellationToken);
}