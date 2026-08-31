using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Contracts;

public interface IAuditGrain : IGrainWithIntegerKey
{
    const long SingletonKey = 0;

    Task<AuditRecordMessage> AppendAsync(
        AppendAuditRecordMessage request,
        GrainCancellationToken cancellationToken);

    Task<ListAuditRecordsActorResponse> ListAsync(
        ListAuditRecordsActorRequest request,
        GrainCancellationToken cancellationToken);

    Task<AuditStatusMessage> GetStatusAsync();
}