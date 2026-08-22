namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class SubscriptionsScenario : BenchmarkScenario
{
    public SubscriptionsScenario() : base("subscriptions",
        "Append-to-observation latency and cursor continuity.", ScheduleMode.CorrelatedWorkflow,
        ScheduleMode.CorrelatedWorkflow, ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop)
    { }

    protected override IReadOnlyList<string> RequiredOperationCoverage => ["subscriptions:reconnect"];

    protected override Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        runtime.DefaultMeasurementOperationLimit(1);
        return Task.CompletedTask;
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.SubscriptionAsync(database, sequence, measure, cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken) => runtime.VerifySubscriptionObservationsAsync(errors, cancellationToken);
}