namespace NativeDCB.Server.Security;

internal sealed class ApiKeyBootstrapService(ApiKeyStore store, ILogger<ApiKeyBootstrapService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        CreatedApiKey? bootstrap = await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (bootstrap is null)
        {
            return;
        }

        logger.LogWarning(
            "A one-time bootstrap API key was generated. Create a normal administrative key before serving traffic.");
        Console.Error.WriteLine("NativeDCB bootstrap API key (shown once):");
        Console.Error.WriteLine(bootstrap.Credential);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }
}