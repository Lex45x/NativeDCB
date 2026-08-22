namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class HotInventoryScenario : BenchmarkScenario
{
    public HotInventoryScenario() : base("hot-inventory",
        "Many carts reserve one finite SKU; commits and domain rejections.", ScheduleMode.SynchronizedBurst,
        ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop, ScheduleMode.SynchronizedBurst)
    { }

    protected override async Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        runtime.DefaultMeasurementOperationLimit(runtime.FiniteCohortSize());
        await runtime.PrepareHotSlotsAsync(runtime.MeasurementOperationLimit!.Value, measure: true,
            cancellationToken);
        await runtime.PrepareHotSlotsAsync(runtime.DatabaseCount, measure: false, cancellationToken);
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.HotInventoryAsync(sequence, measure, cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken)
    {
        runtime.VerifyHotInventory(errors);
        return Task.CompletedTask;
    }
}