using System.Collections.Concurrent;
using System.Diagnostics;

using Grpc.Core;

using NativeDCB.Commerce.Workloads;
using NativeDCB.Protocol.V1;

namespace NativeDCB.SystemBenchmarks.Metrics;

internal sealed class RunMetrics
{
    private readonly bool _openLoop;
    private readonly ConcurrentDictionary<string, long> _outcomes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, long>> _operationOutcomes = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Latencies> _latencies = new(StringComparer.Ordinal);
    private long _offered, _scheduled, _started, _completed, _failed, _dropped, _inFlight, _maxInFlight, _events;
    private long _maxCommittedEventsPerOperation;
    private long _firstStartedUtcTicks, _lastCompletedUtcTicks, _streamEvents, _streamFinalEventId;
    private long _subscriptionObserved, _subscriptionMaximumCursorLag;

    public RunMetrics(bool openLoop) => _openLoop = openLoop;

    public long Failed => Volatile.Read(ref _failed);
    public long CommittedEvents => Volatile.Read(ref _events);
    public long Completed => Volatile.Read(ref _completed);
    public long Dropped => Volatile.Read(ref _dropped);
    public long OperationOutcomeCount(string operation, CommerceSemanticOutcome outcome) =>
        _operationOutcomes.TryGetValue(operation, out ConcurrentDictionary<string, long>? values)
            ? values.GetValueOrDefault(OutcomeName(outcome)) : 0;
    public IReadOnlySet<string> OperationNames => _operationOutcomes.Keys.ToHashSet(StringComparer.Ordinal);
    public DateTimeOffset? FirstStartedUtc => Volatile.Read(ref _firstStartedUtcTicks) == 0 ? null :
        new DateTimeOffset(Volatile.Read(ref _firstStartedUtcTicks), TimeSpan.Zero);
    public DateTimeOffset? LastCompletedUtc => Volatile.Read(ref _lastCompletedUtcTicks) == 0 ? null :
        new DateTimeOffset(Volatile.Read(ref _lastCompletedUtcTicks), TimeSpan.Zero);
    public TimeSpan MeasurementDuration => FirstStartedUtc.HasValue && LastCompletedUtc.HasValue
        ? LastCompletedUtc.Value - FirstStartedUtc.Value : TimeSpan.Zero;

    public void PlanOpenLoop(int offered) => Interlocked.Exchange(ref _offered, offered);

    public void OpenArrivalScheduled(TimeSpan schedulerDelay, bool dropped)
    {
        Interlocked.Increment(ref _scheduled);
        _latencies.GetOrAdd("open-loop-scheduler-delay", _ => new Latencies())
            .Record(Math.Max(0, schedulerDelay.TotalMilliseconds));
        if (dropped) Interlocked.Increment(ref _dropped);
    }

    public void ScenarioAccepted()
    {
        Interlocked.Increment(ref _offered);
        Interlocked.Increment(ref _scheduled);
    }

    public long Start()
    {
        Interlocked.CompareExchange(ref _firstStartedUtcTicks, DateTimeOffset.UtcNow.UtcTicks, 0);
        Interlocked.Increment(ref _started);
        long active = Interlocked.Increment(ref _inFlight);
        long maximum;
        while (active > (maximum = Volatile.Read(ref _maxInFlight)))
            if (Interlocked.CompareExchange(ref _maxInFlight, active, maximum) == maximum) break;
        return Stopwatch.GetTimestamp();
    }

    public void Complete(string operation, string outcome, long started, int committedEvents = 0)
    {
        _outcomes.AddOrUpdate(outcome, 1, (_, value) => value + 1);
        _operationOutcomes.GetOrAdd(operation, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal))
            .AddOrUpdate(outcome, 1, (_, value) => value + 1);
        _latencies.GetOrAdd(operation, _ => new Latencies()).Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Interlocked.Add(ref _events, committedEvents);
        long maximum;
        while (committedEvents > (maximum = Volatile.Read(ref _maxCommittedEventsPerOperation)))
            if (Interlocked.CompareExchange(ref _maxCommittedEventsPerOperation, committedEvents, maximum) == maximum) break;
        Finish();
    }

    public void Fail(string operation, Exception exception, long started)
    {
        string outcome = exception switch
        {
            RpcException rpc => $"transport:{rpc.StatusCode}",
            UnexpectedSemanticOutcomeException => "unexpected-semantic-outcome",
            _ => $"client:{exception.GetType().Name}"
        };
        _outcomes.AddOrUpdate(outcome, 1, (_, value) => value + 1);
        _operationOutcomes.GetOrAdd(operation, _ => new ConcurrentDictionary<string, long>(StringComparer.Ordinal))
            .AddOrUpdate(outcome, 1, (_, value) => value + 1);
        _latencies.GetOrAdd(operation, _ => new Latencies()).Record(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        Interlocked.Increment(ref _failed);
        Finish();
    }

    public void RecordStep(string step, TimeSpan elapsed) =>
        _latencies.GetOrAdd(step, _ => new Latencies()).Record(elapsed.TotalMilliseconds);

    public void RecordStream(long count, long finalEventId)
    {
        Interlocked.Add(ref _streamEvents, count);
        long current;
        while (finalEventId > (current = Volatile.Read(ref _streamFinalEventId)))
            if (Interlocked.CompareExchange(ref _streamFinalEventId, finalEventId, current) == current) break;
    }

    public void RecordSubscription(long observedCount, long cursorLag)
    {
        Interlocked.Add(ref _subscriptionObserved, observedCount);
        long maximum;
        while (cursorLag > (maximum = Volatile.Read(ref _subscriptionMaximumCursorLag)))
            if (Interlocked.CompareExchange(ref _subscriptionMaximumCursorLag, cursorLag, maximum) == maximum) break;
    }

    public object Snapshot(TimeSpan duration) => new
    {
        offered = Volatile.Read(ref _offered),
        offeredSource = _openLoop ? "repository planned-arrival clock" : "repository scenario callback",
        scheduled = Volatile.Read(ref _scheduled),
        started = Volatile.Read(ref _started),
        completed = Volatile.Read(ref _completed),
        dropped = Volatile.Read(ref _dropped),
        dropPolicy = _openLoop ? "drop when a planned arrival reaches a worker at least one second late" : null,
        failed = Volatile.Read(ref _failed),
        active = Volatile.Read(ref _inFlight),
        maxInFlight = Volatile.Read(ref _maxInFlight),
        committedEvents = Volatile.Read(ref _events),
        committedEventsPerSecond = duration.TotalSeconds > 0 ? Volatile.Read(ref _events) / duration.TotalSeconds : 0,
        maxCommittedEventsPerOperation = Volatile.Read(ref _maxCommittedEventsPerOperation),
        throughputPerSecond = duration.TotalSeconds > 0 ? Volatile.Read(ref _completed) / duration.TotalSeconds : 0,
        streamEvents = Volatile.Read(ref _streamEvents),
        streamFinalEventId = Volatile.Read(ref _streamFinalEventId),
        streamEventsPerSecond = duration.TotalSeconds > 0 ? Volatile.Read(ref _streamEvents) / duration.TotalSeconds : 0,
        subscriptionObservedEvents = Volatile.Read(ref _subscriptionObserved),
        subscriptionMaximumCursorLag = Volatile.Read(ref _subscriptionMaximumCursorLag),
        outcomes = _outcomes.OrderBy(x => x.Key).ToDictionary(),
        operationOutcomes = _operationOutcomes.OrderBy(x => x.Key).ToDictionary(
            x => x.Key, x => x.Value.OrderBy(y => y.Key).ToDictionary()),
        latencies = _latencies.OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Value.Snapshot())
    };

    public static int CommittedEventCount(ExecuteHandlerResponse response) =>
        response.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Committed ? response.Committed.Events.Count : 0;

    public static int CommittedEventCount(CompleteDecisionResponse response) =>
        response.OutcomeCase == CompleteDecisionResponse.OutcomeOneofCase.Committed ? response.Committed.Events.Count : 0;

    private void Finish()
    {
        Interlocked.Increment(ref _completed);
        Interlocked.Exchange(ref _lastCompletedUtcTicks, DateTimeOffset.UtcNow.UtcTicks);
        Interlocked.Decrement(ref _inFlight);
    }

    private static string OutcomeName(CommerceSemanticOutcome outcome) => outcome.ToString().ToLowerInvariant();

    private sealed class Latencies
    {
        private const int Capacity = 1_000_000;
        private readonly double[] _values = new double[Capacity];
        private long _count;
        private double _maximum;

        public void Record(double milliseconds)
        {
            long index = Interlocked.Increment(ref _count) - 1;
            _values[index % Capacity] = milliseconds;
            double maximum;
            while (milliseconds > (maximum = Volatile.Read(ref _maximum)))
                if (Interlocked.CompareExchange(ref _maximum, milliseconds, maximum) == maximum) break;
        }

        public object Snapshot()
        {
            long total = Volatile.Read(ref _count);
            int count = (int)Math.Min(total, Capacity);
            double[] copy = new double[count];
            Array.Copy(_values, copy, count);
            Array.Sort(copy);
            double P(double percentile) => count == 0 ? 0 : copy[Math.Min(count - 1, (int)Math.Ceiling(percentile * count) - 1)];
            return new { count = total, sampled = count, p50Ms = P(.5), p90Ms = P(.9), p95Ms = P(.95), p99Ms = P(.99), p999Ms = P(.999), maxMs = Volatile.Read(ref _maximum) };
        }
    }
}

internal sealed class UnexpectedSemanticOutcomeException(string operation, CommerceSemanticOutcome actual, CommerceOutcomeExpectation expected)
    : InvalidOperationException($"{operation} returned {actual}; expected {expected.Name} ({string.Join(", ", expected.Allowed)}).")
{
    public string Operation { get; } = operation;
    public CommerceSemanticOutcome Actual { get; } = actual;
    public CommerceOutcomeExpectation Expected { get; } = expected;
}