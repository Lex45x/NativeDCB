namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class PartitionRolloverScenario : BenchmarkScenario
{
    public PartitionRolloverScenario() : base("partition-rollover",
        "Writes with a deliberately small partition limit and state work.", ScheduleMode.ClosedLoop,
        ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop, ScheduleMode.SynchronizedBurst)
    { }

    public override int DefaultPartitionLimit => 16;
    public override long MinimumOperations(int databases, int partitionLimit) =>
        ((long)partitionLimit + 1) * databases;

    protected override void ValidateScenario(BenchmarkOptions options)
    {
        long defaultBudget = Math.Max((long)options.PartitionLimit * 2, 32L) * options.Databases;
        if (!options.Operations.HasValue && defaultBudget > int.MaxValue)
            throw new ArgumentException($"Partition rollover default operation budget {defaultBudget} exceeds the supported maximum of {int.MaxValue}; specify a smaller --partition-limit or --databases value.");
    }

    protected override Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        long perDatabase = Math.Max((long)runtime.Options.PartitionLimit * 2, 32L);
        runtime.DefaultMeasurementOperationLimit(checked((int)(perDatabase * runtime.DatabaseCount)));
        return Task.CompletedTask;
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.PartitionRolloverAsync(database, sequence, measure, cancellationToken);

    public override Task BeforeMeasurementAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken) =>
        runtime.CaptureMeasurementStorageBaselineAsync(cancellationToken);

    public override Task WaitForVerificationReadinessAsync(BenchmarkRuntime runtime,
        CancellationToken cancellationToken) => runtime.WaitForRolloverStateAsync(cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken) => runtime.VerifyPartitionRolloverAsync(errors, cancellationToken);
}