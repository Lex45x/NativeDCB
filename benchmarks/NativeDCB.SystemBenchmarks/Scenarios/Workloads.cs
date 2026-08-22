using System.Diagnostics;

using Grpc.Core;

using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Events;
using NativeDCB.Commerce.Models;
using NativeDCB.Commerce.Workloads;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;
using NativeDCB.SystemBenchmarks.Metrics;

using NBomber.Contracts;
using NBomber.CSharp;

namespace NativeDCB.SystemBenchmarks.Scenarios;

internal sealed class Workloads
{
    private static readonly CommerceOutcomeExpectation CommittedOnly =
        new("committed", CommerceSemanticOutcome.Committed);
    private static readonly CommerceOutcomeExpectation ReconciledOnly =
        new("reconciled", CommerceSemanticOutcome.Reconciled);
    private readonly BenchmarkRuntime _runtime;

    public Workloads(BenchmarkRuntime runtime) => _runtime = runtime;
    public RunMetrics Metrics => _runtime.Metrics;
    internal int DatabaseCount => _runtime.DatabaseCount;

    public Task WarmAsync(CancellationToken cancellationToken) =>
        _runtime.Options.Strategy.WarmAsync(this, cancellationToken);

    internal async Task WarmRecoveryAsync(CancellationToken cancellationToken)
    {
        await _runtime.Client.GetHeadAsync(_runtime.FirstDatabase, cancellationToken);
        int count = 0;
        await foreach (SequencedEvent _ in _runtime.Client.ReadEventsByRangeAsync(
                           _runtime.FirstDatabase, limit: 1, cancellationToken: cancellationToken)) count++;
        if (count != 1) throw new InvalidOperationException("Recovery warmup finite read returned no event.");
    }

    public async Task<IResponse> RunAsync(bool measure, CancellationToken cancellationToken,
        bool plannedArrivalBoundary = false)
    {
        long sequence = _runtime.NextSequence(measure);
        string database = _runtime.Database(sequence);
        if (measure && !plannedArrivalBoundary) _runtime.Metrics.ScenarioAccepted();
        long started = measure ? _runtime.Metrics.Start() : 0;
        string operation = _runtime.Options.Strategy.Slug;
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_runtime.Options.Strategy.OperationTimeout(_runtime.Options));
        try
        {
            OperationResult result = await _runtime.Options.Strategy.ExecuteAsync(
                this, database, sequence, measure, timeout.Token);
            operation = result.Operation;
            if (measure) _runtime.Metrics.Complete(result.Operation, result.Outcome, started, result.Events);
            return Response.Ok<object?>(null, statusCode: result.StatusCode, sizeBytes: 0);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || timeout.IsCancellationRequested)
        {
            if (!measure) throw;
            _runtime.Metrics.Fail(operation, exception, started);
            string status = exception switch
            {
                RpcException rpc => $"transport:{rpc.StatusCode}",
                UnexpectedSemanticOutcomeException => "unexpected-semantic-outcome",
                _ => $"client:{exception.GetType().Name}"
            };
            return Response.Fail<object?>(null, "Benchmark operation failed.", status, sizeBytes: 0);
        }
    }

    internal async Task<OperationResult> IndependentWriteAsync(long n, bool measure, CancellationToken token)
    {
        PreparedBenchmarkSlot prepared = _runtime.PreparedSlot(n, measure);
        ExecuteHandlerResponse response = await Command(prepared.Database,
            new ReserveCartLine(prepared.Workload.CartId, prepared.Workload.LineId, prepared.Workload.CustomerId,
                prepared.Sku, prepared.WarehouseId, 1), "independent-reserve", measure,
            prepared.Workload.CommandId(5), CommerceExpectedOutcomes.IndependentWrite, token);
        return Result("independent-write", response);
    }

    internal async Task<OperationResult> HotInventoryAsync(long n, bool measure, CancellationToken token)
    {
        PreparedBenchmarkSlot prepared = _runtime.PreparedSlot(n, measure);
        ExecuteHandlerResponse response = await Command(prepared.Database,
            new ReserveCartLine(prepared.Workload.CartId, prepared.Workload.LineId, prepared.Workload.CustomerId,
                prepared.Sku, prepared.WarehouseId, 1), "hot-reserve", measure,
            prepared.Workload.CommandId(3), CommerceExpectedOutcomes.HotInventory, token);
        return Result("hot-inventory", response);
    }

    internal async Task<OperationResult> ReservationCheckoutAsync(long n, bool measure, CancellationToken token)
    {
        PreparedBenchmarkSlot prepared = _runtime.PreparedSlot(n, measure);
        Task<ExecuteHandlerResponse> reserve = Command(prepared.Database,
            new ReserveCartLine(prepared.Workload.CartId, prepared.Workload.LineId, prepared.Workload.CustomerId,
                prepared.Sku, prepared.WarehouseId, 1), "race-reserve", measure,
            prepared.Workload.CommandId(3), CommerceExpectedOutcomes.ReservationVersusCheckout, token);
        Task<ExecuteHandlerResponse> checkout = Command(prepared.Database,
            new CheckoutCart(prepared.Workload.CartId, prepared.Workload.OrderId, prepared.Workload.CustomerId),
            "race-checkout", measure, prepared.Workload.CommandId(4),
            CommerceExpectedOutcomes.ReservationVersusCheckout, token);
        ExecuteHandlerResponse[] responses = await Task.WhenAll(reserve, checkout);
        if (measure) _runtime.RecordReservationPair(prepared, responses[0], responses[1]);
        CommerceSemanticOutcome[] outcomes = responses.Select(CommerceOutcomes.Classify).ToArray();
        return new OperationResult("reservation-checkout", "success",
            string.Join("+", outcomes.Select(Name).Order()), responses.Sum(RunMetrics.CommittedEventCount));
    }

    internal async Task<OperationResult> DuplicateCommandAsync(string database, bool measure, CancellationToken token)
    {
        string suffix = measure ? "measure" : "warm";
        Guid commandId = _runtime.CommandId(measure, "duplicate", 1);
        ExecuteHandlerResponse response = await Timed("duplicate-submit", measure, () =>
            _runtime.Client.ExecuteHandlerAsync(database, nameof(PublishProduct),
                new PublishProduct($"duplicate-product-{suffix}", "Duplicate", 100, "USD"), commandId, token));
        CommerceSemanticOutcome outcome = Require("duplicate-submit", CommerceOutcomes.Classify(response),
            CommerceExpectedOutcomes.DuplicateCommand);
        if (measure) _runtime.RecordDuplicate(database, outcome);
        return new OperationResult("duplicate-command", Name(outcome), Name(outcome), RunMetrics.CommittedEventCount(response));
    }

    internal async Task<OperationResult> CompletePurchaseAsync(string database, long n, bool measure, CancellationToken token)
    {
        CommerceWorkloadSlot slot = Slot(n, measure);
        List<ExecuteHandlerResponse> results =
        [
            await Command(database, new RegisterCustomer(slot.CustomerId, slot.CustomerId), "register-customer", measure, slot.CommandId(1), CommittedOnly, token),
            await Command(database, new OpenCart(slot.CartId, slot.CustomerId, "USD"), "open-cart", measure, slot.CommandId(2), CommittedOnly, token),
            await Command(database, new ReserveCartLine(slot.CartId, slot.LineId, slot.CustomerId, slot.Sku, _runtime.StockedWarehouse(slot.Sku), 1), "reserve-line", measure, slot.CommandId(3), CommittedOnly, token),
            await Command(database, new CheckoutCart(slot.CartId, slot.OrderId, slot.CustomerId), "checkout", measure, slot.CommandId(4), CommittedOnly, token),
            await Command(database, new AuthorizePayment(slot.PaymentId, slot.OrderId), "authorize-payment", measure, slot.CommandId(5), CommittedOnly, token),
            await Command(database, new CreateShipment(slot.ShipmentId, slot.OrderId, slot.CustomerId, "Benchmark carrier"), "create-shipment", measure, slot.CommandId(6), CommittedOnly, token),
            await Command(database, new ShipOrder(slot.ShipmentId, slot.OrderId, slot.CustomerId, $"TRACK-{n:D12}"), "ship-order", measure, slot.CommandId(7), CommittedOnly, token),
            await Command(database, new DeliverOrder(slot.ShipmentId, slot.OrderId, slot.CustomerId), "deliver-order", measure, slot.CommandId(8), CommittedOnly, token)
        ];
        return Aggregate("complete-purchase", results);
    }

    internal async Task<OperationResult> RemotePaymentAsync(string database, long n, bool measure, CancellationToken token)
    {
        CommerceWorkloadSlot slot = Slot(n, measure);
        List<ExecuteHandlerResponse> setup = await SetupOrder(database, slot, measure, token);
        AuthorizePayment command = new(slot.PaymentId, slot.OrderId);
        PreparedDecision<PaymentPreparationModel> prepared = await Timed("prepare-payment", measure, () =>
            _runtime.Client.PrepareDecisionAsync<AuthorizePayment, PaymentPreparationModel>(database,
                nameof(AuthorizePayment), command, slot.CommandId(5), token));
        if (!prepared.IsPrepared)
            throw new UnexpectedSemanticOutcomeException("prepare-payment", CommerceSemanticOutcome.Failed,
                CommerceExpectedOutcomes.RemoteCompletion);
        PaymentAuthorized proposed = new(command.PaymentId, slot.OrderId, prepared.Model.PayableAmountMinor,
            prepared.Model.Currency ?? "USD", $"gateway-{n:D12}");
        ProposedDecisionEvent decisionEvent = new(nameof(PaymentAuthorized), proposed);
        int variant = (int)(n % 4);
        int events = setup.Sum(RunMetrics.CommittedEventCount);
        if (variant == 1)
        {
            ExecuteHandlerResponse unrelated = await Command(database,
                new PublishProduct($"remote-unrelated-{(measure ? "measure" : "warm")}-{n:D12}", "Unrelated", 1, "USD"), "unrelated-movement",
                measure, slot.CommandId(6), CommittedOnly, token);
            events += RunMetrics.CommittedEventCount(unrelated);
        }
        if (variant == 2)
        {
            PreparedDecision<PaymentPreparationModel> stale = await Timed("prepare-stale-payment", measure, () =>
                _runtime.Client.PrepareDecisionAsync<AuthorizePayment, PaymentPreparationModel>(database,
                    nameof(AuthorizePayment), new AuthorizePayment($"{slot.PaymentId}-stale", slot.OrderId),
                    slot.CommandId(7), token));
            if (!stale.IsPrepared) throw new UnexpectedSemanticOutcomeException("prepare-stale-payment",
                CommerceSemanticOutcome.Failed, CommerceExpectedOutcomes.RemoteCompletion);
            CompleteDecisionResponse committed = await Complete(database, prepared, decisionEvent,
                "complete-payment", measure, CommittedOnly, token);
            CompleteDecisionResponse staleResult = await Complete(database, stale,
                new ProposedDecisionEvent(nameof(PaymentAuthorized), proposed with { PaymentId = $"{slot.PaymentId}-stale" }),
                "complete-stale-payment", measure,
                new CommerceOutcomeExpectation("stale", CommerceSemanticOutcome.Stale), token);
            return new OperationResult("remote-payment:matching-stale", "success",
                $"{Name(CommerceOutcomes.Classify(committed))}+{Name(CommerceOutcomes.Classify(staleResult))}",
                events + RunMetrics.CommittedEventCount(committed));
        }
        if (variant == 3)
        {
            CommerceOutcomeExpectation raceExpected = new("concurrent completion",
                CommerceSemanticOutcome.Committed, CommerceSemanticOutcome.Reconciled);
            CompleteDecisionResponse[] raced = await Task.WhenAll(
                Complete(database, prepared, decisionEvent, "complete-payment-a", measure, raceExpected, token),
                Complete(database, prepared, decisionEvent, "complete-payment-b", measure, raceExpected, token));
            if (raced.Count(x => CommerceOutcomes.Classify(x) == CommerceSemanticOutcome.Committed) != 1 ||
                raced.Count(x => CommerceOutcomes.Classify(x) == CommerceSemanticOutcome.Reconciled) != 1)
                throw new UnexpectedSemanticOutcomeException("concurrent-completion", CommerceSemanticOutcome.Failed, raceExpected);
            return new OperationResult("remote-payment:concurrent", "success",
                "committed+reconciled", events + raced.Sum(RunMetrics.CommittedEventCount));
        }
        CompleteDecisionResponse completed = await Complete(database, prepared, decisionEvent,
            "complete-payment", measure, CommittedOnly, token);
        return new OperationResult(variant == 1 ? "remote-payment:unrelated" : "remote-payment:success",
            "success", Name(CommerceOutcomes.Classify(completed)),
            events + RunMetrics.CommittedEventCount(completed));
    }

    internal async Task<OperationResult> QueriesAsync(string database, long n, bool measure, CancellationToken token)
    {
        BenchmarkRuntime.QueryFixture fixture = _runtime.GetQueryFixture(database);
        int variant = (int)(n % 6);
        if (variant < 2)
        {
            EventQuery query = variant == 0 ? fixture.Inventory : fixture.Order;
            IReadOnlyList<long> expected = variant == 0 ? fixture.InventoryExpected : fixture.OrderExpected;
            IReadOnlyList<long> eventual = await Timed("query-eventual-current", measure, () =>
                _runtime.ReadQueryIds(database, query, QueryConsistency.EventualIndex, token));
            IReadOnlyList<long> committed = await Timed("query-committed-current", measure, () =>
                _runtime.ReadQueryIds(database, query, QueryConsistency.CommittedScan, token));
            if (!eventual.SequenceEqual(expected) || !committed.SequenceEqual(expected))
                throw new InvalidOperationException("Current eventual and committed query results differ from the seeded baseline.");
            return Success(variant == 0 ? "queries:current-inventory" : "queries:current-order");
        }

        if (variant is 2 or 3 or 4)
        {
            string fault = variant == 2 ? "stale" : variant == 3 ? "missing" : "corrupt";
            IReadOnlyList<long> actual = await Timed($"query-eventual-{fault}-fallback", measure, () =>
                _runtime.ReadDerivedFallbackVariant(database, fixture.Inventory, fixture.InventoryExpected,
                    fault, token));
            if (!actual.SequenceEqual(fixture.InventoryExpected))
                throw new InvalidOperationException($"{fault} fallback result changed after verification.");
            return Success($"queries:{fault}-index" + (fault == "stale" ? "" : "-fallback"));
        }

        EventQuery typeOnly = new([new NativeDCB.Model.Queries.QueryItem([nameof(ProductPublished)], [])]);
        IReadOnlyList<long> fallback = await Timed("query-eventual-unsupported-shape-fallback", measure, () =>
            _runtime.ReadQueryIds(database, typeOnly, QueryConsistency.EventualIndex, token));
        if (!fallback.SequenceEqual(fixture.ProductTypeExpected))
            throw new InvalidOperationException("The authoritative type-only fallback differed from the finite range baseline.");
        return Success("queries:unsupported-index-shape-fallback");
    }

    internal async Task<OperationResult> RangeStreamingAsync(string database, long n, bool measure, CancellationToken token)
    {
        uint limit = n % 2 == 0 ? 10U : 1_000U;
        long head = (await _runtime.Client.GetHeadAsync(database, token)).EventId;
        int count = 0;
        long finalEventId = 0, totalStart = Stopwatch.GetTimestamp(), first = 0, previous = 0;
        double interItemMilliseconds = 0;
        await foreach (SequencedEvent value in _runtime.Client.ReadEventsByRangeAsync(database,
                           throughEventId: head, limit: limit, cancellationToken: token))
        {
            long current = Stopwatch.GetTimestamp();
            if (count++ == 0) first = current;
            else interItemMilliseconds += Stopwatch.GetElapsedTime(previous, current).TotalMilliseconds;
            previous = current;
            finalEventId = value.EventId;
        }
        int expectedCount = (int)Math.Min(head, limit);
        long expectedFinal = expectedCount;
        if (count != expectedCount || finalEventId != expectedFinal)
            throw new InvalidOperationException($"Range returned count/final {count}/{finalEventId}; expected {expectedCount}/{expectedFinal}.");
        if (measure)
        {
            if (first != 0) _runtime.Metrics.RecordStep("stream-first-item", Stopwatch.GetElapsedTime(totalStart, first));
            if (count > 1) _runtime.Metrics.RecordStep("stream-inter-item-average", TimeSpan.FromMilliseconds(interItemMilliseconds / (count - 1)));
            _runtime.Metrics.RecordStep("stream-total", Stopwatch.GetElapsedTime(totalStart));
            _runtime.Metrics.RecordStream(count, finalEventId);
        }
        return Success(limit == 10 ? "range-streaming:small" : "range-streaming:large");
    }

    internal async Task<OperationResult> SubscriptionAsync(string database, long n, bool measure, CancellationToken token)
    {
        CommerceWorkloadSlot slot = Slot(n, measure);
        long cursor = (await _runtime.Client.GetHeadAsync(database, token)).EventId;
        string sku = $"subscription-{(measure ? "measure" : "warm")}-sku-{n:D12}";
        Task<(SequencedEvent Event, long ObservedAt)> firstObserved = ReadOne(database, cursor, sku, token);
        ExecuteHandlerResponse published = await Command(database,
            new PublishProduct(sku, "Subscription product", 1_000, "USD"), "subscription-publish",
            measure, slot.CommandId(1), CommittedOnly, token);
        long firstCommitAt = Stopwatch.GetTimestamp();
        (SequencedEvent first, long firstObservedAt) = await firstObserved;
        if (first.EventId != published.Committed.LastEventId || first.EventId <= cursor)
            throw new InvalidOperationException("Initial subscription did not observe its correlated commit after the retained cursor.");

        Task<(SequencedEvent Event, long ObservedAt)> reconnected = ReadOne(database, first.EventId, sku, token);
        ExecuteHandlerResponse changed = await Command(database,
            new ChangeProductPrice(sku, 1_001, "USD"), "subscription-reconnect-publish",
            measure, slot.CommandId(2), CommittedOnly, token);
        long secondCommitAt = Stopwatch.GetTimestamp();
        (SequencedEvent second, long secondObservedAt) = await reconnected;
        if (second.EventId != changed.Committed.LastEventId || second.EventId <= first.EventId)
            throw new InvalidOperationException("Reconnected subscription did not observe its correlated commit after the retained cursor.");
        if (measure)
        {
            _runtime.Metrics.RecordStep("subscription-delivery",
                firstObservedAt >= firstCommitAt ? Stopwatch.GetElapsedTime(firstCommitAt, firstObservedAt) : TimeSpan.Zero);
            _runtime.Metrics.RecordStep("subscription-reconnect-delivery",
                secondObservedAt >= secondCommitAt ? Stopwatch.GetElapsedTime(secondCommitAt, secondObservedAt) : TimeSpan.Zero);
            _runtime.RecordSubscription(database, cursor, first.EventId, second.EventId);
        }
        return new OperationResult("subscriptions:reconnect", "success", "committed",
            RunMetrics.CommittedEventCount(published) + RunMetrics.CommittedEventCount(changed));
    }

    internal Task<OperationResult> MixedAsync(string database, long n, bool measure, CancellationToken token) => (n % 10) switch
    {
        0 or 1 => CompletePurchaseAsync(database, n, measure, token),
        2 or 3 => HotInventoryAsync(n, measure, token),
        4 or 5 => MixedQuery(database, measure, token),
        6 => RangeStreamingAsync(database, n, measure, token),
        7 => RemotePaymentAsync(database, n, measure, token),
        8 => SubscriptionAsync(database, n, measure, token),
        _ => ExecutePublish(database, n, measure, "mixed:independent-write", token)
    };

    private async Task<OperationResult> MixedQuery(string database, bool measure, CancellationToken token)
    {
        BenchmarkRuntime.QueryFixture fixture = _runtime.GetQueryFixture(database);
        IReadOnlyList<long> eventual = await Timed("mixed-query-eventual", measure, () =>
            _runtime.ReadQueryIds(database, fixture.Order, QueryConsistency.EventualIndex, token));
        IReadOnlyList<long> committed = await Timed("mixed-query-committed", measure, () =>
            _runtime.ReadQueryIds(database, fixture.Order, QueryConsistency.CommittedScan, token));
        if (!eventual.SequenceEqual(fixture.OrderExpected) || !committed.SequenceEqual(fixture.OrderExpected))
            throw new InvalidOperationException("Mixed order query differed from its isolated seeded baseline.");
        return Success("mixed:queries");
    }

    internal Task<OperationResult> PartitionRolloverAsync(string database, long n, bool measure,
        CancellationToken token) => ExecutePublish(database, n, measure, "partition-rollover", token);

    private async Task<OperationResult> ExecutePublish(string database, long n, bool measure, string operation, CancellationToken token)
    {
        ExecuteHandlerResponse response = await Command(database,
            new PublishProduct($"{(measure ? "measure" : "warm")}-{operation.Replace(':', '-')}-sku-{n:D12}", "Benchmark product", 1_000, "USD"),
            operation, measure, _runtime.CommandId(measure, operation, n), CommittedOnly, token);
        return Result(operation, response);
    }

    internal async Task<OperationResult> RecoveryAsync(string database, long n, bool measure, CancellationToken token)
    {
        await Timed("recovery-stop", measure, async () => { await _runtime.StopForRecoveryAsync(); return true; });
        await Timed("recovery-process-start", measure, async () => { await _runtime.LaunchForRecoveryAsync(token); return true; });
        await Timed("recovery-liveness", measure, async () => { await _runtime.WaitForLivenessAndReconnectAsync(token); return true; });
        await Timed("recovery-database-readiness", measure, async () => { await _runtime.WaitForDatabaseReadinessAsync(token); return true; });
        await Timed("recovery-first-finite-read", measure, async () =>
        {
            int count = 0;
            await foreach (SequencedEvent _ in _runtime.Client.ReadEventsByRangeAsync(database, limit: 1, cancellationToken: token)) count++;
            if (count != 1) throw new InvalidOperationException("Recovery first finite read did not return one event.");
            return count;
        });
        await Command(database, _runtime.RecoveryActivationCommand, "recovery-writer-activation", measure,
            _runtime.RecoveryActivationCommandId, ReconciledOnly, token);
        ExecuteHandlerResponse durable = await Command(database,
            new PublishProduct($"recovery-new-{(measure ? "measure" : "warm")}-{n:D12}", "Recovery new", 1, "USD"),
            "recovery-new-durable-write", measure, _runtime.CommandId(measure, "recovery-new", n), CommittedOnly, token);
        ExecuteHandlerResponse reconciled = await Command(database, _runtime.RecoveryOldCommand,
            "recovery-old-command-reconciliation", measure, _runtime.RecoveryOldCommandId, ReconciledOnly, token);
        GetEventsByCommandIdResponse oldEvents = await Timed("recovery-old-command-read", measure, () =>
            _runtime.Client.GetEventsByCommandIdAsync(database, _runtime.RecoveryOldCommandId, token));
        if (oldEvents.Events.Count != 1) throw new InvalidOperationException("Old recovery command did not resolve to exactly one event.");
        return new OperationResult("recovery", "success",
            $"{Name(CommerceOutcomes.Classify(durable))}+{Name(CommerceOutcomes.Classify(reconciled))}",
            RunMetrics.CommittedEventCount(durable));
    }

    private async Task<List<ExecuteHandlerResponse>> SetupOrder(string database, CommerceWorkloadSlot slot,
        bool measure, CancellationToken token) =>
    [
        await Command(database, new RegisterCustomer(slot.CustomerId, slot.CustomerId), "register-customer", measure, slot.CommandId(1), CommittedOnly, token),
        await Command(database, new OpenCart(slot.CartId, slot.CustomerId, "USD"), "open-cart", measure, slot.CommandId(2), CommittedOnly, token),
        await Command(database, new ReserveCartLine(slot.CartId, slot.LineId, slot.CustomerId, slot.Sku, _runtime.StockedWarehouse(slot.Sku), 1), "reserve-line", measure, slot.CommandId(3), CommittedOnly, token),
        await Command(database, new CheckoutCart(slot.CartId, slot.OrderId, slot.CustomerId), "checkout", measure, slot.CommandId(4), CommittedOnly, token)
    ];

    private async Task<ExecuteHandlerResponse> Command<T>(string database, T command, string step, bool measure,
        Guid commandId, CommerceOutcomeExpectation expected, CancellationToken token) where T : notnull
    {
        ExecuteHandlerResponse response = await Timed(step, measure, () =>
            _runtime.Client.ExecuteHandlerAsync(database, typeof(T).Name, command, commandId, token));
        Require(step, CommerceOutcomes.Classify(response), expected);
        return response;
    }

    private async Task<CompleteDecisionResponse> Complete<TModel>(string database, PreparedDecision<TModel> prepared,
        ProposedDecisionEvent proposed, string step, bool measure, CommerceOutcomeExpectation expected,
        CancellationToken token)
    {
        CompleteDecisionResponse response = await Timed(step, measure, () =>
            _runtime.Client.CompleteDecisionAsync(database, prepared.ModelSignature, [proposed], token));
        Require(step, CommerceOutcomes.Classify(response), expected);
        return response;
    }

    private async Task<T> Timed<T>(string step, bool measure, Func<Task<T>> operation)
    {
        long started = Stopwatch.GetTimestamp();
        T result = await operation();
        if (measure) _runtime.Metrics.RecordStep(step, Stopwatch.GetElapsedTime(started));
        return result;
    }

    private CommerceWorkloadSlot Slot(long n, bool measure) => CommerceWorkloadSlot.Create(
        _runtime.Fixture, CommerceCommandIds.ScopeForSeed(_runtime.Options.Seed + (measure ? 90_001 : 100_001)),
        measure ? n : n + 1_000_000);

    private async Task<(SequencedEvent Event, long ObservedAt)> ReadOne(string database, long cursor, string sku, CancellationToken token)
    {
        await foreach (SequencedEvent value in _runtime.Client.SubscribeEventsAsync(database, cursor,
                           keys: [new EventKey("sku", sku)], cancellationToken: token))
            return (value, Stopwatch.GetTimestamp());
        throw new InvalidOperationException("Subscription ended before observing an event.");
    }

    private static CommerceSemanticOutcome Require(string operation, CommerceSemanticOutcome actual,
        CommerceOutcomeExpectation expected)
    {
        if (!expected.Allows(actual)) throw new UnexpectedSemanticOutcomeException(operation, actual, expected);
        return actual;
    }

    private static OperationResult Result(string operation, ExecuteHandlerResponse response)
    {
        CommerceSemanticOutcome outcome = CommerceOutcomes.Classify(response);
        return new OperationResult(operation, Name(outcome), Name(outcome), RunMetrics.CommittedEventCount(response));
    }

    private static OperationResult Aggregate(string operation, IEnumerable<ExecuteHandlerResponse> responses)
    {
        ExecuteHandlerResponse[] values = responses.ToArray();
        return new OperationResult(operation, "committed",
            string.Join("+", values.Select(value => Name(CommerceOutcomes.Classify(value))).Distinct()),
            values.Sum(RunMetrics.CommittedEventCount));
    }

    private static OperationResult Success(string operation) =>
        new(operation, "success", "success", 0);

    private static string Name(CommerceSemanticOutcome outcome) => outcome.ToString().ToLowerInvariant();

}

internal sealed record OperationResult(
    string Operation,
    string Outcome,
    string StatusCode,
    int Events);