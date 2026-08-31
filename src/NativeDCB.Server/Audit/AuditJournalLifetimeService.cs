using NativeDCB.Engine.Storage.Audit;

namespace NativeDCB.Server.Audit;

internal sealed class AuditJournalLifetimeService(AuditJournal journal) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        return journal.InitializeAsync(cancellationToken);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}