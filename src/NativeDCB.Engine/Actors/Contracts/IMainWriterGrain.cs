namespace NativeDCB.Engine.Actors;

public interface IMainWriterGrain : IGrainWithStringKey
{
    Task<WriterStateMessage> GetStateAsync(GrainCancellationToken cancellationToken);

    Task<AppendResultMessage> AppendAsync(
        EventBatchMessage batch,
        AppendConditionMessage condition,
        GrainCancellationToken cancellationToken);
}