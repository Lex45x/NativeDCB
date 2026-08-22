namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class DuplicateCommandScenario : BenchmarkScenario
{
    public DuplicateCommandScenario() : base("duplicate-command",
        "Simultaneous bounded submission and reconciliation of one command ID.", ScheduleMode.SynchronizedBurst,
        ScheduleMode.SynchronizedBurst)
    { }

    public override int DefaultConcurrency(int databases) => Math.Max(2, databases * 2);
    public override long MinimumOperations(int databases, int partitionLimit) => (long)databases * 2;

    protected override void ValidateScenarioSchedule(ScheduleMode mode, IReadOnlyList<int> concurrency,
        int databases)
    {
        if (mode != ScheduleMode.SynchronizedBurst)
            throw new ArgumentException("The duplicate-command scenario requires --mode burst to guarantee simultaneous attempts.");
        if (concurrency.Any(value => value < databases * 2))
            throw new ArgumentException("Duplicate command requires concurrency of at least two times --databases.");
    }

    protected override Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        if (!runtime.Options.Operations.HasValue)
            runtime.EnsureMeasurementOperationLimit(Math.Max(runtime.DatabaseCount * 2, runtime.Concurrency));
        runtime.SetMeasurementOperationLimit(runtime.RoundUpToConcurrency(
            runtime.MeasurementOperationLimit ?? Math.Max(runtime.DatabaseCount * 2, runtime.Concurrency)));
        return Task.CompletedTask;
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.DuplicateCommandAsync(database, measure, cancellationToken);

    protected override Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken) => runtime.VerifyDuplicateCommandAsync(errors, cancellationToken);
}