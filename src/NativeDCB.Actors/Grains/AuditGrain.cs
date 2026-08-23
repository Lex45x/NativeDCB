using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Engine.Storage.Audit;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class AuditGrain(AuditJournal journal) : Grain, IAuditGrain
{
    public override async Task OnActivateAsync(CancellationToken cancellationToken)
    {
        await journal.InitializeAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        await base.OnActivateAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    public async Task<AuditRecordMessage> AppendAsync(
        AppendAuditRecordMessage request,
        GrainCancellationToken cancellationToken)
    {
        AuditRecord record = await journal.AppendAsync(ToDraft(request), cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return ToMessage(record);
    }

    public async Task<ListAuditRecordsActorResponse> ListAsync(
        ListAuditRecordsActorRequest request,
        GrainCancellationToken cancellationToken)
    {
        AuditPage page = await journal.QueryAsync(
                new AuditQuery(
                    request.AfterSequence,
                    request.Limit,
                    request.Database,
                    request.Operation,
                    request.Phase,
                    request.Outcome,
                    request.AuthenticationScheme,
                    request.Subject),
                cancellationToken.CancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        return new ListAuditRecordsActorResponse(
            page.Records.Select(ToMessage).ToArray(),
            page.NextAfterSequence,
            page.HasMore,
            page.BoundarySequence);
    }

    public Task<AuditStatusMessage> GetStatusAsync()
    {
        AuditJournalStatus status = journal.GetStatus();
        return Task.FromResult(new AuditStatusMessage(
            status.Ready,
            status.LastSequence,
            status.ActivePartition,
            status.Fault));
    }

    private static AuditRecordDraft ToDraft(AppendAuditRecordMessage value)
    {
        return new AuditRecordDraft(
            value.OperationId,
            value.Phase,
            value.Category,
            value.Operation,
            value.AuthenticationScheme,
            value.Subject,
            value.Issuer,
            value.Database,
            value.Resource,
            value.Outcome,
            value.Code,
            value.GrpcStatus,
            value.TraceId,
            value.CommandId,
            value.FirstEventId,
            value.LastEventId,
            value.Revision);
    }

    private static AuditRecordMessage ToMessage(AuditRecord value)
    {
        return new AuditRecordMessage(
            value.Sequence,
            value.TimestampUtc,
            value.OperationId,
            value.Phase,
            value.Category,
            value.Operation,
            value.AuthenticationScheme,
            value.Subject,
            value.Issuer,
            value.Database,
            value.Resource,
            value.Outcome,
            value.Code,
            value.GrpcStatus,
            value.TraceId,
            value.CommandId,
            value.FirstEventId,
            value.LastEventId,
            value.Revision,
            value.PreviousHash,
            value.RecordHash);
    }
}