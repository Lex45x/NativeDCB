using NativeDCB.SystemBenchmarks.Hosting;
using NativeDCB.SystemBenchmarks.Metrics;
using NativeDCB.SystemBenchmarks.Reporting;
using NativeDCB.SystemBenchmarks.Scenarios;
using NativeDCB.SystemBenchmarks.Scheduling;

namespace NativeDCB.SystemBenchmarks;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        ParseResult parsed;
        try { parsed = CommandLine.Parse(args); }
        catch (ArgumentException exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            Console.Error.WriteLine("Use --help for usage or --list-scenarios for the catalog.");
            return 2;
        }
        if (parsed.ShowHelp) { CommandLine.PrintHelp(); return 0; }
        if (parsed.ListScenarios) { ScenarioCatalog.Print(); return 0; }

        BenchmarkOptions options = parsed.Options!;
        using CancellationTokenSource shutdown = new();
        Console.CancelKeyPress += (_, eventArgs) => { eventArgs.Cancel = true; shutdown.Cancel(); };
        int exitCode = 0;
        foreach (int concurrency in options.Concurrency)
        {
            try
            {
                if (!await RunOnceAsync(options, concurrency, shutdown.Token)) exitCode = 1;
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
            {
                Console.Error.WriteLine("Benchmark cancelled after writing its failure bundle.");
                return 130;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"Unhandled benchmark failure: {exception}");
                exitCode = 1;
            }
        }
        return exitCode;
    }

    private static async Task<bool> RunOnceAsync(BenchmarkOptions options, int concurrency, CancellationToken token)
    {
        ResultBundle bundle = ResultBundle.Create(options, concurrency);
        DateTimeOffset runStarted = DateTimeOffset.UtcNow;
        ServerProcess? server = null;
        RunMetrics metrics = new(options.Mode == ScheduleMode.OpenLoop);
        BenchmarkRuntime? runtime = null;
        ProcessMetricsSampler? processMetrics = null;
        ProfilerSession? profiler = null;
        string phase = "initialization";
        List<string> errors = [];
        IReadOnlyList<object> databaseStatus = [];
        long? fixtureHead = null, measurementStartHead = null, endHead = null, fixtureBytes = null;
        DateTimeOffset? measurementStarted = null, measurementEnded = null;
        bool valid = false, cancelled = false;
        SchedulerResult? schedulerResult = null;

        Console.WriteLine($"Result bundle: {bundle.Root}");
        try
        {
            Directory.CreateDirectory(bundle.StorageRoot);
            server = new ServerProcess(options, bundle.StorageRoot, bundle.ServerLog);
            phase = "server-start";
            await server.StartAsync(token);
            runtime = new BenchmarkRuntime(options, concurrency, server, metrics);

            phase = "commerce-setup";
            await runtime.SetupAsync(token);
            fixtureHead = await runtime.AggregateHeadAsync(token);
            fixtureBytes = ResultBundle.GetExistingFileBytes(bundle.StorageRoot);
            await bundle.WriteManifestAsync(options, server, concurrency, runStarted, null, fixtureHead, fixtureBytes);

            Workloads workloads = new(runtime);
            phase = "warmup";
            Console.WriteLine($"Warmup {options.Warmup}; measurement {options.Duration}; mode {CommandLine.ModeName(options.Mode)}.");
            await Scheduler.WarmAsync(workloads, options.Warmup, token);
            measurementStartHead = await runtime.AggregateHeadAsync(token);
            await options.Strategy.BeforeMeasurementAsync(runtime, token);

            phase = "metrics-start";
            processMetrics = new ProcessMetricsSampler(() => server.CurrentProcess, bundle.ProcessMetrics);
            await processMetrics.StartAsync();
            profiler = new ProfilerSession(options.Profiling, server.Process.Id, bundle.Root, options.Duration);
            await profiler.StartAsync(token);

            phase = "measurement";
            schedulerResult = Scheduler.Run(options, concurrency, workloads, runtime.MeasurementOperationLimit,
                bundle.NBomberRoot, token);
            if (schedulerResult.FailedIterations != 0)
                errors.Add($"NBomber reported {schedulerResult.FailedIterations} failed scenario iterations.");
            measurementStarted = metrics.FirstStartedUtc;
            measurementEnded = metrics.LastCompletedUtc;

            phase = "metrics-stop";
            await profiler.StopAsync(token);
            await profiler.DisposeAsync();
            profiler = null;
            await processMetrics.DisposeAsync();
            processMetrics = null;

            phase = "derived-state-readiness";
            await options.Strategy.WaitForVerificationReadinessAsync(runtime, token);
            phase = "verification";
            ScenarioVerification verification = await runtime.VerifyAsync(measurementStartHead.Value, token);
            valid = verification.Valid;
            errors.AddRange(verification.Errors);
            endHead = verification.EndHead;
            databaseStatus = verification.DatabaseStatus;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            cancelled = true;
            errors.Add($"{phase}: cancelled");
        }
        catch (Exception exception)
        {
            errors.Add($"{phase}: {exception.GetType().Name}: {exception.Message}");
            Console.Error.WriteLine($"Benchmark failed during {phase}: {exception.Message}");
        }
        finally
        {
            if (profiler is not null)
            {
                try { await profiler.DisposeAsync(); }
                catch (Exception exception) { errors.Add($"profiler-cleanup: {exception.Message}"); }
            }
            if (processMetrics is not null)
            {
                try { await processMetrics.DisposeAsync(); }
                catch (Exception exception) { errors.Add($"process-metrics-cleanup: {exception.Message}"); }
            }
            try { runtime?.Dispose(); }
            catch (Exception exception) { errors.Add($"runtime-dispose: {exception.Message}"); }
            try { if (server is not null) await server.StopAsync(); }
            catch (Exception exception) { errors.Add($"server-stop: {exception.Message}"); }

            try { await bundle.WriteStorageSummaryAsync(bundle.StorageRoot); }
            catch (Exception exception) { errors.Add($"storage-summary: {exception.Message}"); }
            try
            {
                if (server is not null)
                    await bundle.WriteManifestAsync(options, server, concurrency, runStarted, DateTimeOffset.UtcNow,
                        fixtureHead, fixtureBytes);
            }
            catch (Exception exception) { errors.Add($"manifest: {exception.Message}"); }

            try { if (server is not null) await server.DisposeAsync(); }
            catch (Exception exception) { errors.Add($"server-dispose: {exception.Message}"); }
            if (!options.RetainStorage)
            {
                try { await ResultBundle.DeleteWithRetryAsync(bundle.StorageRoot); }
                catch (Exception exception) { errors.Add($"storage-cleanup: {exception.Message}"); }
            }

            valid = valid && errors.Count == 0;
            TimeSpan metricsDuration = runtime?.MeasurementOperationLimit is null
                ? options.Duration
                : metrics.MeasurementDuration;
            object summary = new
            {
                schemaVersion = 1,
                scenario = options.Scenario,
                profile = options.Profile.ToString().ToLowerInvariant(),
                mode = CommandLine.ModeName(options.Mode),
                concurrency,
                offeredRate = options.Rate,
                operations = runtime?.MeasurementOperationLimit ?? options.Operations,
                databaseCount = options.Databases,
                seed = options.Seed,
                fixture = options.FixtureProfile.ToString().ToLowerInvariant(),
                phaseCompleted = phase,
                measurement = new
                {
                    startedUtc = measurementStarted,
                    endedUtc = measurementEnded,
                    termination = runtime?.MeasurementOperationLimit is null ? "duration" : "finite-iterations",
                    configuredDurationSeconds = runtime?.MeasurementOperationLimit is null ? options.Duration.TotalSeconds : (double?)null,
                    observedOperationWindowSeconds = metrics.MeasurementDuration.TotalSeconds,
                    finiteOperationLimit = runtime?.MeasurementOperationLimit
                },
                metrics = metrics.Snapshot(metricsDuration),
                nbomber = schedulerResult,
                correctness = new { valid, cancelled, errors },
                heads = new { fixture = fixtureHead, measurementStart = measurementStartHead, end = endHead },
                databaseStatus,
                artifacts = new
                {
                    manifest = "manifest.json",
                    storage = "storage-summary.json",
                    serverLog = "server.log",
                    processMetrics = "process-metrics.csv",
                    nbomber = "nbomber/",
                    profiler = options.Profiling == ProfilingMode.None ? null : options.Profiling == ProfilingMode.Trace ? "optional.nettrace" :
                        options.Profiling == ProfilingMode.GcDump ? "optional.gcdump" : "optional.counters.csv"
                }
            };
            try { await bundle.WriteSummaryAsync(summary); }
            catch (Exception exception) { Console.Error.WriteLine($"Could not write summary.json: {exception.Message}"); valid = false; }
        }

        Console.WriteLine(valid
            ? $"Verified {options.Scenario}; head {measurementStartHead} -> {endHead}."
            : $"INVALID {options.Scenario}: {string.Join("; ", errors)}");
        if (cancelled) throw new OperationCanceledException(token);
        return valid;
    }
}