namespace NativeDCB.SystemBenchmarks.Scenarios.Strategies;

internal sealed class RecoveryScenario : BenchmarkScenario
{
    public RecoveryScenario() : base("recovery",
        "Restart, readiness, first read/write, and command reconciliation.", ScheduleMode.CorrelatedWorkflow,
        ScheduleMode.CorrelatedWorkflow)
    { }

    public override bool SupportsProfiling => false;
    public override string ServerLoggingLevel => "Information";

    protected override void ValidateScenarioSchedule(ScheduleMode mode, IReadOnlyList<int> concurrency,
        int databases)
    {
        if (concurrency.Count != 1 || concurrency[0] != 1)
            throw new ArgumentException("The recovery scenario requires --concurrency 1 because it owns server restart lifecycle.");
    }

    protected override void ValidateScenarioOperations(int? operations)
    {
        if (operations is not null and not 1)
            throw new ArgumentException("Recovery requires --operations 1.");
    }

    protected override async Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        runtime.SetMeasurementOperationLimit(1);
        await runtime.PrepareRecoveryAsync(cancellationToken);
    }

    public override Task WarmAsync(Workloads workloads, CancellationToken cancellationToken) =>
        workloads.WarmRecoveryAsync(cancellationToken);

    public override TimeSpan OperationTimeout(BenchmarkOptions options) =>
        options.StartupTimeout + options.RpcTimeout;

    public override Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken) =>
        workloads.RecoveryAsync(database, sequence, measure, cancellationToken);
}