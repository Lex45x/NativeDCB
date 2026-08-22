namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class IndependentWritesScenario : BenchmarkScenario
{
    public IndependentWritesScenario() : base("independent-writes",
        "Unique products and command IDs; durable writer capacity.", ScheduleMode.ClosedLoop,
        ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop, ScheduleMode.SynchronizedBurst)
    { }

    protected override async Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        runtime.DefaultMeasurementOperationLimit(runtime.FiniteCohortSize());
        await runtime.PrepareIndependentSlotsAsync(runtime.MeasurementOperationLimit!.Value, measure: true,
            cancellationToken);
        await runtime.PrepareIndependentSlotsAsync(runtime.DatabaseCount, measure: false, cancellationToken);
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.IndependentWriteAsync(sequence, measure, cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken)
    {
        runtime.VerifyIndependentWrites(errors);
        return Task.CompletedTask;
    }
}