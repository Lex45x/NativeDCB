namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class QueriesScenario : BenchmarkScenario
{
    public QueriesScenario() : base("queries",
        "Current and derived-fault fallback Commerce queries.", ScheduleMode.ClosedLoop,
        ScheduleMode.ClosedLoop)
    { }

    public override long MinimumOperations(int databases, int partitionLimit) => 6;
    public override bool SupportsProfiling => false;
    public override int WarmupOperationCount(int databases) => 1;
    public override TimeSpan OperationTimeout(BenchmarkOptions options) =>
        options.StartupTimeout + options.RpcTimeout;
    protected override IReadOnlyList<string> RequiredOperationCoverage =>
        ["queries:current-inventory", "queries:current-order", "queries:stale-index", "queries:missing-index-fallback", "queries:corrupt-index-fallback", "queries:unsupported-index-shape-fallback"];

    protected override void ValidateScenarioSchedule(ScheduleMode mode, IReadOnlyList<int> concurrency,
        int databases)
    {
        if (mode != ScheduleMode.ClosedLoop || concurrency.Count != 1 || concurrency[0] != 1)
            throw new ArgumentException("Queries requires --mode closed --concurrency 1 because derived fault variants restart the server.");
    }

    protected override async Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        if (!runtime.Options.Operations.HasValue) runtime.EnsureMeasurementOperationLimit(6);
        await runtime.PrepareQueriesAsync(cancellationToken);
    }

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.QueriesAsync(database, sequence, measure, cancellationToken);
}