namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class MixedScenario : BenchmarkScenario
{
    public MixedScenario() : base("mixed",
        "Weighted purchases, reservations, reads, remote payment, and subscriptions.", ScheduleMode.ClosedLoop,
        ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop, ScheduleMode.CorrelatedWorkflow)
    { }

    public override long MinimumOperations(int databases, int partitionLimit) => 10;
    protected override IReadOnlyList<string> RequiredOperationCoverage =>
        ["complete-purchase", "hot-inventory", "mixed:queries", "range-streaming:small", "remote-payment:concurrent", "subscriptions:reconnect", "mixed:independent-write"];

    protected override async Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        int finiteCount = runtime.FiniteCohortSize();
        if (!runtime.Options.Operations.HasValue)
            runtime.EnsureMeasurementOperationLimit(Math.Max(20, finiteCount));
        runtime.DefaultMeasurementOperationLimit(finiteCount);
        await runtime.PrepareHotSlotsAsync(runtime.MeasurementOperationLimit!.Value, measure: true,
            cancellationToken);
        await runtime.PrepareHotSlotsAsync(runtime.DatabaseCount, measure: false, cancellationToken);
        await runtime.PrepareQueriesAsync(cancellationToken);
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.MixedAsync(database, sequence, measure, cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken) => runtime.VerifySubscriptionObservationsAsync(errors, cancellationToken);
}