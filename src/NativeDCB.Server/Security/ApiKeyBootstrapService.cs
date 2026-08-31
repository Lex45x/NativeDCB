using NativeDCB.Engine.Storage.Audit;

namespace NativeDCB.Server.Security;

internal sealed class ApiKeyBootstrapService(
    ApiKeyStore store,
    AuditJournal audit,
    ILogger<ApiKeyBootstrapService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await audit.InitializeAsync(cancellationToken).ConfigureAwait(false);
        Guid operationId = Guid.NewGuid();
        await audit.AppendAsync(SystemRecord(operationId, "attempt"), cancellationToken).ConfigureAwait(false);
        CreatedApiKey? bootstrap;
        try
        {
            bootstrap = await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await audit.AppendAsync(SystemRecord(operationId, "outcome", "cancelled"), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception)
        {
            await audit.AppendAsync(SystemRecord(
                    operationId, "outcome", "failed", "unexpected"), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        await audit.AppendAsync(SystemRecord(
                operationId, "outcome", bootstrap is null ? "unchanged" : "created"), cancellationToken)
            .ConfigureAwait(false);

        if (bootstrap is null)
        {
            return;
        }

        logger.LogWarning(
            "A one-time bootstrap API key was generated. Create a normal administrative key before serving traffic.");
        await Console.Error.WriteLineAsync("NativeDCB bootstrap API key (shown once):").ConfigureAwait(false);
        await Console.Error.WriteLineAsync(bootstrap.Credential).ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    private static AuditRecordDraft SystemRecord(
        Guid operationId,
        string phase,
        string? outcome = null,
        string? code = null)
    {
        return new AuditRecordDraft(
            operationId,
            phase,
            "security",
            "system/auth/bootstrap",
            AuthenticationScheme: "System",
            Subject: "native-dcb-server",
            Outcome: outcome,
            Code: code);
    }
}