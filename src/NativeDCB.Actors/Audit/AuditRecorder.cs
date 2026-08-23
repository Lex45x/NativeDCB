using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;

namespace NativeDCB.Actors.Audit;

public static class AuditRecorder
{
    public static async Task OutcomeAsync(
        IGrainFactory grains,
        string outcome,
        string? code = null,
        string? commandId = null,
        long? firstEventId = null,
        long? lastEventId = null,
        long? revision = null)
    {
        AuditOperationContextMessage? context = AuditRequestContext.Current;
        if (context is null)
        {
            return;
        }

        using GrainCancellationTokenSource source = new();
        try
        {
            await grains.GetGrain<IAuditGrain>(IAuditGrain.SingletonKey).AppendAsync(
                    new AppendAuditRecordMessage(
                        context.OperationId,
                        "outcome",
                        context.Category,
                        context.Operation,
                        context.AuthenticationScheme,
                        context.Subject,
                        context.Issuer,
                        context.Database,
                        context.Resource,
                        outcome,
                        code,
                        TraceId: context.TraceId,
                        CommandId: commandId ?? context.CommandId,
                        FirstEventId: firstEventId,
                        LastEventId: lastEventId,
                        Revision: revision),
                    source.Token)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            throw new IOException("The audit journal is unavailable.");
        }
    }
}