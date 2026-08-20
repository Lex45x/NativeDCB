using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IIndexOrchestratorGrain : IGrainWithStringKey
{
    Task<IndexListResultMessage> ListIndexesAsync(GrainCancellationToken cancellationToken);

    Task<AdministrationOperationResultMessage> RequestIndexRebuildAsync(
        string eventType,
        EventKeyMessage[] keys,
        GrainCancellationToken cancellationToken);

    Task<IndexQueryResultMessage> ReadAuthoritativeAsync(
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken);

    Task<IndexQueryResultMessage> ReadEventualAsync(
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        GrainCancellationToken cancellationToken);
}