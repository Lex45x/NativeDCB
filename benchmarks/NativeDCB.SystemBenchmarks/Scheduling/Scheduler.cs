using System.Diagnostics;

using NativeDCB.SystemBenchmarks.Scenarios;

using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

namespace NativeDCB.SystemBenchmarks.Scheduling;

internal sealed record SchedulerResult(long Requests, long FailedIterations, long SuccessfulIterations);

internal static class Scheduler
{
    public static async Task WarmAsync(Workloads workloads, TimeSpan duration, CancellationToken cancellationToken)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        await workloads.WarmAsync(cancellationToken);
        TimeSpan remaining = duration - (DateTimeOffset.UtcNow - started);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, cancellationToken);
    }

    public static SchedulerResult Run(BenchmarkOptions options, int concurrency, Workloads workloads,
        int? operationLimit, string reportFolder, CancellationToken cancellationToken)
    {
        if (options.Mode == ScheduleMode.OpenLoop && !operationLimit.HasValue)
            throw new InvalidOperationException("Open-loop scheduling requires an exact operation budget.");
        if (options.Mode == ScheduleMode.SynchronizedBurst &&
            (!operationLimit.HasValue || operationLimit.Value % concurrency != 0))
            throw new InvalidOperationException("Burst scheduling requires an operation budget divisible by concurrency.");

        CohortBarrier? barrier = options.Mode == ScheduleMode.SynchronizedBurst
            ? new CohortBarrier(concurrency, cancellationToken)
            : null;
        PlannedOpenLoop? openLoop = options.Mode == ScheduleMode.OpenLoop
            ? new PlannedOpenLoop(options.Rate!.Value, operationLimit!.Value, workloads)
            : null;
        if (openLoop is not null) openLoop.Plan();

        ScenarioProps scenario = Scenario.Create(options.Scenario, async _ =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (openLoop is not null) return await openLoop.ExecuteNextAsync(cancellationToken);
                if (barrier is not null) await barrier.SignalAndWaitAsync();
                return await workloads.RunAsync(measure: true, cancellationToken);
            })
            .WithoutWarmUp()
            .WithRestartIterationOnFail(false)
            .WithMaxFailCount(int.MaxValue)
            .WithStatsBasedOnIterations(true)
            .WithLoadSimulations(CreateSimulation(options, concurrency, operationLimit, openLoop?.WorkerCount));

        NodeStats stats = NBomberRunner.RegisterScenarios(scenario)
            .WithTestSuite("NativeDCB.SystemBenchmarks")
            .WithTestName($"{options.Scenario}-{options.Profile.ToString().ToLowerInvariant()}-{CommandLine.ModeName(options.Mode)}")
            .WithReportFolder(reportFolder)
            .WithReportFileName("report")
            .WithReportFormats(ReportFormat.Html, ReportFormat.Csv, ReportFormat.Txt, ReportFormat.Md)
            .Run();
        ScenarioStats scenarioStats = stats.ScenarioStats.Single(value => value.ScenarioName == options.Scenario);
        long failed = scenarioStats.Fail.Request.Count;
        long succeeded = scenarioStats.Ok.Request.Count;
        return new SchedulerResult(failed + succeeded, failed, succeeded);
    }

    private static LoadSimulation CreateSimulation(BenchmarkOptions options, int concurrency,
        int? operationLimit, int? openLoopWorkers)
    {
        if (openLoopWorkers.HasValue)
            return Simulation.IterationsForConstant(openLoopWorkers.Value, operationLimit!.Value);
        if (operationLimit.HasValue) return Simulation.IterationsForConstant(concurrency, operationLimit.Value);
        return Simulation.KeepConstant(concurrency, options.Duration);
    }

    private sealed class PlannedOpenLoop
    {
        private static readonly TimeSpan DropThreshold = TimeSpan.FromSeconds(1);
        private readonly int _rate;
        private readonly int _operations;
        private readonly Workloads _workloads;
        private long _nextOrdinal;
        private long _startTimestamp;

        public PlannedOpenLoop(int rate, int operations, Workloads workloads)
        {
            _rate = rate;
            _operations = operations;
            _workloads = workloads;
            WorkerCount = OpenLoopPlan.WorkerCount(rate);
        }

        public int WorkerCount { get; }

        public void Plan() => _workloads.Metrics.PlanOpenLoop(_operations);

        public async Task<IResponse> ExecuteNextAsync(CancellationToken cancellationToken)
        {
            long ordinal = Interlocked.Increment(ref _nextOrdinal) - 1;
            if (ordinal >= _operations) throw new InvalidOperationException("NBomber requested more arrivals than planned.");
            long now = Stopwatch.GetTimestamp();
            long start = Interlocked.CompareExchange(ref _startTimestamp, now, 0);
            if (start == 0) start = now;
            long due = start + (long)Math.Round(ordinal * (double)Stopwatch.Frequency / _rate);
            TimeSpan untilDue = Stopwatch.GetElapsedTime(now, due);
            if (untilDue > TimeSpan.Zero) await Task.Delay(untilDue, cancellationToken);
            TimeSpan delay = Stopwatch.GetElapsedTime(due, Stopwatch.GetTimestamp());
            bool dropped = delay >= DropThreshold;
            _workloads.Metrics.OpenArrivalScheduled(delay, dropped);
            if (dropped) return Response.Ok<object?>(null, statusCode: "open-loop-dropped", sizeBytes: 0);
            return await _workloads.RunAsync(measure: true, cancellationToken, plannedArrivalBoundary: true);
        }
    }

    private sealed class CohortBarrier
    {
        private readonly int _participants;
        private readonly object _lock = new();
        private readonly CancellationTokenRegistration _cancellation;
        private int _waiting;
        private TaskCompletionSource _generation = NewGeneration();

        public CohortBarrier(int participants, CancellationToken cancellationToken)
        {
            _participants = participants;
            _cancellation = cancellationToken.Register(Abort);
        }

        public Task SignalAndWaitAsync()
        {
            lock (_lock)
            {
                Task task = _generation.Task;
                if (++_waiting == _participants)
                {
                    _waiting = 0;
                    TaskCompletionSource complete = _generation;
                    _generation = NewGeneration();
                    complete.TrySetResult();
                }
                return task;
            }
        }

        private void Abort()
        {
            lock (_lock)
            {
                _waiting = 0;
                _generation.TrySetCanceled();
                _generation = NewGeneration();
            }
        }

        private static TaskCompletionSource NewGeneration() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}