namespace NativeDCB.Server.Decisions.Transactions;

internal static class GrainCall
{
    public static async Task<T> RunAsync<T>(
        Func<GrainCancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        using GrainCancellationTokenSource source = new();
        CancellationState cancellation = new(source);
        try
        {
            await using CancellationTokenRegistration registration = cancellationToken.Register(
                static state => ((CancellationState)state!).Cancel(), cancellation);
            return await call(source.Token).WaitAsync(cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        finally
        {
            await cancellation.CancellationTask.ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private sealed class CancellationState(GrainCancellationTokenSource source)
    {
        public Task CancellationTask { get; private set; } = Task.CompletedTask;

        public void Cancel()
        {
            CancellationTask = source.Cancel();
        }
    }
}