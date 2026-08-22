namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class RemotePaymentScenario : BenchmarkScenario
{
    public RemotePaymentScenario() : base("remote-payment",
        "Measured prepare/complete capabilities, including stale completion.", ScheduleMode.CorrelatedWorkflow,
        ScheduleMode.CorrelatedWorkflow, ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop)
    { }

    public override long MinimumOperations(int databases, int partitionLimit) => 4;
    protected override IReadOnlyList<string> RequiredOperationCoverage =>
        ["remote-payment:success", "remote-payment:unrelated", "remote-payment:matching-stale", "remote-payment:concurrent"];

    protected override Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        if (!runtime.Options.Operations.HasValue) runtime.EnsureMeasurementOperationLimit(4);
        return Task.CompletedTask;
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.RemotePaymentAsync(database, sequence, measure, cancellationToken);
}