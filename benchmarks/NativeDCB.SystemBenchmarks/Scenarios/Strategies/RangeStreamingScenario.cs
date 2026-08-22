namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class RangeStreamingScenario : BenchmarkScenario
{
    public RangeStreamingScenario() : base("range-streaming",
        "Small and large snapshot streams, first-item and total latency.", ScheduleMode.ClosedLoop,
        ScheduleMode.ClosedLoop, ScheduleMode.OpenLoop)
    { }

    public override long MinimumOperations(int databases, int partitionLimit) => 2;
    protected override IReadOnlyList<string> RequiredOperationCoverage =>
        ["range-streaming:small", "range-streaming:large"];

    protected override Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        if (!runtime.Options.Operations.HasValue) runtime.EnsureMeasurementOperationLimit(2);
        return Task.CompletedTask;
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.RangeStreamingAsync(database, sequence, measure, cancellationToken);
}