namespace NativeDCB.SystemBenchmarks.Scenarios;

internal abstract class BenchmarkScenario
{
    protected BenchmarkScenario(string slug, string description, ScheduleMode defaultMode,
        params ScheduleMode[] supportedModes)
    {
        Slug = slug;
        Description = description;
        DefaultMode = defaultMode;
        SupportedModes = supportedModes.ToHashSet();
    }

    public string Slug { get; }
    public string Description { get; }
    public ScheduleMode DefaultMode { get; }
    public IReadOnlySet<ScheduleMode> SupportedModes { get; }
    public virtual int DefaultConcurrency(int databases) => 1;
    public virtual long MinimumOperations(int databases, int partitionLimit) => 1;
    public virtual int DefaultPartitionLimit => 10_000;
    public virtual bool SupportsProfiling => true;
    public virtual string ServerLoggingLevel => "Warning";
    protected virtual IReadOnlyList<string> RequiredOperationCoverage => [];

    public void ValidateSchedule(ScheduleMode mode, IReadOnlyList<int> concurrency, int databases)
    {
        if (!SupportedModes.Contains(mode))
            throw new ArgumentException($"Scenario '{Slug}' does not support mode '{CommandLine.ModeName(mode)}'. Supported: {string.Join(", ", SupportedModes.Select(CommandLine.ModeName))}.");
        ValidateScenarioSchedule(mode, concurrency, databases);
    }

    protected virtual void ValidateScenarioSchedule(ScheduleMode mode, IReadOnlyList<int> concurrency,
        int databases)
    { }

    public void ValidateOperations(int? operations, int databases, int partitionLimit)
    {
        long minimum = MinimumOperations(databases, partitionLimit);
        if (minimum > int.MaxValue)
            throw new ArgumentException($"{Slug} requires {minimum} operations for this configuration, exceeding the supported maximum of {int.MaxValue}.");
        if (operations < minimum)
            throw new ArgumentException($"{Slug} requires --operations of at least {minimum} for variant/cohort coverage.");
        ValidateScenarioOperations(operations);
    }

    protected virtual void ValidateScenarioOperations(int? operations) { }

    public void Validate(BenchmarkOptions options)
    {
        if (!SupportsProfiling && options.Profiling != ProfilingMode.None)
            throw new ArgumentException($"{Slug} changes the server PID; --profiling must be none until profiler reattachment is supported.");
        ValidateScenario(options);
    }

    protected virtual void ValidateScenario(BenchmarkOptions options) { }

    public async Task SetupAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken)
    {
        await SetupScenarioAsync(runtime, cancellationToken);
        if (runtime.Options.Mode == ScheduleMode.SynchronizedBurst && !runtime.MeasurementOperationLimit.HasValue)
            runtime.SetMeasurementOperationLimit(runtime.RoundUpToConcurrency(
                runtime.Options.Operations ?? runtime.FiniteCohortSize()));
    }

    protected virtual Task SetupScenarioAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public virtual int WarmupOperationCount(int databases) => databases;

    public virtual async Task WarmAsync(Workloads workloads, CancellationToken cancellationToken)
    {
        int count = WarmupOperationCount(workloads.DatabaseCount);
        for (int index = 0; index < count; index++)
            await workloads.RunAsync(measure: false, cancellationToken);
    }

    public virtual TimeSpan OperationTimeout(BenchmarkOptions options) => options.RpcTimeout;

    public abstract Task<OperationResult> ExecuteAsync(Workloads workloads, string database, long sequence,
        bool measure, CancellationToken cancellationToken);

    public virtual Task BeforeMeasurementAsync(BenchmarkRuntime runtime, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    public virtual Task WaitForVerificationReadinessAsync(BenchmarkRuntime runtime,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task VerifyAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken)
    {
        runtime.VerifyFiniteOperationCount(errors);
        runtime.VerifyOperationCoverage(RequiredOperationCoverage, errors);
        await VerifyScenarioAsync(runtime, errors, cancellationToken);
    }

    protected virtual Task VerifyScenarioAsync(BenchmarkRuntime runtime, List<string> errors,
        CancellationToken cancellationToken) => Task.CompletedTask;
}