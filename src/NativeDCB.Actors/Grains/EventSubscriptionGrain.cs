using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Observability;

namespace NativeDCB.Actors.Grains;

// ReSharper disable once UnusedType.Global -- Orleans activates grains by interface at runtime.
public sealed class EventSubscriptionGrain(IGrainFactory grains) : Grain, IEventSubscriptionGrain
{
    private const int MaxBatchSize = 256;
    private const int MaxPollAttempts = 10;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(100);

    private EventSubscriptionOpenMessage? _configuration;
    private long _cursor;
    private int _delivered;
    private bool _closed;
    private bool _meterActive;

    public async Task OpenAsync(
        EventSubscriptionOpenMessage request,
        GrainCancellationToken cancellationToken)
    {
        if (_configuration is not null || _closed)
        {
            throw new InvalidOperationException("The event subscription has already been opened.");
        }

        if (request.AfterEventId < 0 || request.Limit is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (!Enum.IsDefined(request.Kind))
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        await grains.GetGrain<IReadGrain>(request.Database)
            .ReadRangeSnapshotAsync(
                request.AfterEventId,
                throughEventIdInclusive: null,
                maxCount: 1,
                cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);

        _configuration = request;
        _cursor = request.AfterEventId;
        _meterActive = true;
        ActorTelemetry.ActiveSubscriptions.Add(1,
            new KeyValuePair<string, object?>("kind", Kind(request.Kind)));
    }

    public async Task<EventSubscriptionReadResultMessage> ReadNextAsync(
        EventSubscriptionReadNextMessage request,
        GrainCancellationToken cancellationToken)
    {
        EventSubscriptionOpenMessage configuration = _configuration ??
            throw new InvalidOperationException("The event subscription has not been opened.");
        if (_closed)
        {
            return new EventSubscriptionReadResultMessage(_cursor, [], Completed: true);
        }

        if (request.MaxCount is <= 0 or > MaxBatchSize)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        int remaining = configuration.Limit is null
            ? request.MaxCount
            : Math.Min(request.MaxCount, configuration.Limit.Value - _delivered);
        if (remaining <= 0)
        {
            return new EventSubscriptionReadResultMessage(_cursor, [], Completed: true);
        }

        for (int attempt = 0; attempt < MaxPollAttempts; attempt++)
        {
            cancellationToken.CancellationToken.ThrowIfCancellationRequested();
            EventSubscriptionReadResultMessage result = configuration.Kind == EventSubscriptionKind.Range
                ? await ReadRangeAsync(configuration, remaining, cancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext)
                : await ReadQueryAsync(configuration, remaining, cancellationToken)
                    .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (result.Events.Length > 0 || result.Completed)
            {
                if (result.Events.Length > 0)
                {
                    ActorTelemetry.SubscriptionEvents.Add(result.Events.Length,
                        new KeyValuePair<string, object?>("kind", Kind(configuration.Kind)));
                }

                return result;
            }

            await Task.Delay(PollInterval, cancellationToken.CancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        }

        return new EventSubscriptionReadResultMessage(_cursor, [], Completed: false);
    }

    public Task CloseAsync(
        EventSubscriptionCloseMessage request,
        GrainCancellationToken cancellationToken)
    {
        cancellationToken.CancellationToken.ThrowIfCancellationRequested();
        _closed = true;
        StopMeter();
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    public override Task OnDeactivateAsync(DeactivationReason reason, CancellationToken cancellationToken)
    {
        StopMeter();
        return base.OnDeactivateAsync(reason, cancellationToken);
    }

    private async Task<EventSubscriptionReadResultMessage> ReadRangeAsync(
        EventSubscriptionOpenMessage configuration,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        EventReadSnapshotMessage snapshot = await grains.GetGrain<IReadGrain>(configuration.Database)
            .ReadRangeSnapshotAsync(
                _cursor,
                throughEventIdInclusive: null,
                maxCount,
                cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        SequencedEventMessage[] scanned = snapshot.Events
            .Where(value => value.EventId > _cursor)
            .OrderBy(value => value.EventId)
            .ToArray();
        SequencedEventMessage[] events = scanned
            .GroupBy(value => value.EventId)
            .Select(group => group.First())
            .ToArray();
        Advance(snapshot.ObservedHead, scanned, maxCount);
        return Result(snapshot.ObservedHead, events, configuration.Limit);
    }

    private async Task<EventSubscriptionReadResultMessage> ReadQueryAsync(
        EventSubscriptionOpenMessage configuration,
        int maxCount,
        GrainCancellationToken cancellationToken)
    {
        IndexQueryResultMessage snapshot = await grains
            .GetGrain<IIndexOrchestratorGrain>(configuration.Database)
            .ReadAuthoritativeAsync(
                configuration.Query,
                _cursor,
                throughEventIdInclusive: null,
                maxCount,
                cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
        SequencedEventMessage[] scanned = snapshot.Events
            .Where(value => value.EventId > _cursor)
            .OrderBy(value => value.EventId)
            .ToArray();
        SequencedEventMessage[] events = scanned
            .Where(value => configuration.RequiredKeys.All(required => value.Keys.Contains(required)))
            .GroupBy(value => value.EventId)
            .Select(group => group.First())
            .ToArray();
        Advance(snapshot.ObservedHead, scanned, maxCount);
        return Result(snapshot.ObservedHead, events, configuration.Limit);
    }

    private void Advance(long observedHead, IReadOnlyList<SequencedEventMessage> scanned, int maxCount)
    {
        _cursor = scanned.Count == maxCount
            ? scanned[^1].EventId
            : Math.Max(_cursor, observedHead);
    }

    private EventSubscriptionReadResultMessage Result(
        long observedHead,
        SequencedEventMessage[] events,
        int? limit)
    {
        _delivered += events.Length;
        bool completed = limit is not null && _delivered >= limit.Value;
        return new EventSubscriptionReadResultMessage(observedHead, events, completed);
    }

    private void StopMeter()
    {
        if (!_meterActive || _configuration is null)
        {
            return;
        }

        _meterActive = false;
        ActorTelemetry.ActiveSubscriptions.Add(-1,
            new KeyValuePair<string, object?>("kind", Kind(_configuration.Kind)));
    }

    private static string Kind(EventSubscriptionKind kind)
    {
        return kind == EventSubscriptionKind.Range ? "range" : "query";
    }
}