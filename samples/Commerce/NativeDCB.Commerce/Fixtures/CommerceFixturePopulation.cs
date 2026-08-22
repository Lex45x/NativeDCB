using System.Collections.ObjectModel;
using System.Globalization;
using System.Text.Json;

using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Events;
using NativeDCB.Commerce.Workloads;
using NativeDCB.Model.Events;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;

namespace NativeDCB.Commerce.Fixtures;

public sealed record CommerceFixtureCommand(
    long Ordinal,
    Guid CommandId,
    string HandlerName,
    object Command,
    IReadOnlyList<string> ExpectedEventTypes,
    CommerceOutcomeExpectation ExpectedOutcome);

public sealed record CommerceFixturePopulationResult(
    long StartingHead,
    long EndingHead,
    long CommandCount,
    IReadOnlyDictionary<CommerceSemanticOutcome, long> Outcomes)
{
    public long AppendedEventCount => EndingHead - StartingHead;
}

public static class CommerceFixturePopulation
{
    private const long InitialInventoryQuantity = 1_000_000;

    public static IEnumerable<CommerceFixtureCommand> CreateCommands(CommerceFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);

        long commandOrdinal = 0;
        long eventCount = 0;
        foreach (string sku in fixture.Skus)
        {
            if (eventCount >= fixture.Options.PreloadedEventCount)
            {
                yield break;
            }

            commandOrdinal++;
            eventCount++;
            yield return Create(
                fixture,
                commandOrdinal,
                new PublishProduct(sku, $"Fixture product {sku}", 1_000 + commandOrdinal, "USD"),
                nameof(ProductPublished));
        }

        for (int index = 0; index < fixture.Skus.Length; index++)
        {
            if (eventCount >= fixture.Options.PreloadedEventCount)
            {
                yield break;
            }

            commandOrdinal++;
            eventCount++;
            string sku = fixture.Skus[index];
            yield return Create(
                fixture,
                commandOrdinal,
                new ReceiveInventory(
                    fixture.ReceiptId(index),
                    sku,
                    fixture.SelectWarehouseId(index),
                    InitialInventoryQuantity),
                nameof(InventoryReceived));
        }

        foreach (string customerId in fixture.CustomerIds)
        {
            if (eventCount >= fixture.Options.PreloadedEventCount)
            {
                yield break;
            }

            commandOrdinal++;
            eventCount++;
            yield return Create(
                fixture,
                commandOrdinal,
                new RegisterCustomer(customerId, $"Fixture customer {customerId}"),
                nameof(CustomerRegistered));
        }

        for (int index = 0; index < fixture.CartIds.Length; index++)
        {
            if (eventCount >= fixture.Options.PreloadedEventCount)
            {
                yield break;
            }

            commandOrdinal++;
            eventCount++;
            yield return Create(
                fixture,
                commandOrdinal,
                new OpenCart(fixture.CartIds[index], fixture.CustomerIds[index], "USD"),
                nameof(CartOpened));
        }

        long lifecycleOrdinal = 0;
        while (fixture.Options.PreloadedEventCount - eventCount >= 9)
        {
            int productIndex = (int)(lifecycleOrdinal % fixture.Skus.Length);
            int customerIndex = (int)(lifecycleOrdinal % fixture.CustomerIds.Length);
            string suffix = (lifecycleOrdinal + 1).ToString("D12", CultureInfo.InvariantCulture);
            string sku = fixture.Skus[productIndex];
            string warehouse = fixture.SelectWarehouseId(productIndex);
            string customer = fixture.CustomerIds[customerIndex];
            string cart = $"fixture-history-cart-{suffix}";
            string line = $"fixture-history-line-{suffix}";
            string order = $"fixture-history-order-{suffix}";
            string payment = $"fixture-history-payment-{suffix}";
            string shipment = $"fixture-history-shipment-{suffix}";

            yield return Create(fixture, ++commandOrdinal, new OpenCart(cart, customer, "USD"),
                nameof(CartOpened));
            yield return Create(fixture, ++commandOrdinal,
                new ReserveCartLine(cart, line, customer, sku, warehouse, 1),
                nameof(CartLineReserved), nameof(InventoryReserved));
            yield return Create(fixture, ++commandOrdinal, new CheckoutCart(cart, order, customer),
                nameof(CartCheckedOut), nameof(OrderPlaced));
            yield return Create(fixture, ++commandOrdinal, new AuthorizePayment(payment, order),
                nameof(PaymentAuthorized));
            yield return Create(fixture, ++commandOrdinal,
                new CreateShipment(shipment, order, customer, "Fixture carrier"),
                nameof(ShipmentCreated));
            yield return Create(fixture, ++commandOrdinal,
                new ShipOrder(shipment, order, customer, $"FIXTURE-{suffix}"),
                nameof(OrderShipped));
            yield return Create(fixture, ++commandOrdinal, new DeliverOrder(shipment, order, customer),
                nameof(OrderDelivered));

            eventCount += 9;
            lifecycleOrdinal++;
        }

        while (eventCount < fixture.Options.PreloadedEventCount)
        {
            long sample = eventCount;
            commandOrdinal++;
            eventCount++;
            yield return Create(
                fixture,
                commandOrdinal,
                new AdjustInventory(
                    fixture.AdjustmentId(commandOrdinal - 1),
                    fixture.SelectSku(sample),
                    fixture.SelectWarehouseId(sample),
                    1,
                    "Fixture history"),
                nameof(InventoryAdjusted));
        }
    }

    public static IReadOnlyList<SequencedEvent> CreateHistory(CommerceFixture fixture)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        if (fixture.Options.PreloadedEventCount > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(fixture),
                fixture.Options.PreloadedEventCount,
                "An in-memory fixture history cannot exceed Int32.MaxValue events.");
        }

        List<SequencedEvent> history = new((int)fixture.Options.PreloadedEventCount);
        FixtureHistoryState state = new();
        long eventId = 0;
        foreach (CommerceFixtureCommand command in CreateCommands(fixture))
        {
            IReadOnlyList<CandidateEvent> candidates = ToEvents(command, state);
            if (!candidates.Select(value => value.Type).SequenceEqual(command.ExpectedEventTypes))
            {
                throw new InvalidOperationException(
                    $"Fixture command {command.Ordinal} ({command.HandlerName}) generated an unexpected event batch.");
            }

            foreach (CandidateEvent candidate in candidates)
            {
                eventId++;
                history.Add(new SequencedEvent(
                    eventId,
                    candidate.Type,
                    candidate.Data,
                    candidate.Keys,
                    candidate.SchemaVersion,
                    DateTimeOffset.UnixEpoch.AddTicks(eventId),
                    command.CommandId,
                    command.HandlerName));
            }
        }

        return history.AsReadOnly();
    }

    public static async Task<CommerceFixturePopulationResult> PopulateAsync(
        NativeDcbClient client,
        string database,
        CommerceFixture fixture,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        ArgumentNullException.ThrowIfNull(fixture);

        long startingHead = (await client.GetHeadAsync(database, cancellationToken)).EventId;
        Dictionary<CommerceSemanticOutcome, long> outcomes = [];
        long commandCount = 0;
        foreach (CommerceFixtureCommand command in CreateCommands(fixture))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExecuteHandlerResponse response = await client.ExecuteHandlerAsync(
                database,
                command.HandlerName,
                command.Command,
                command.CommandId,
                cancellationToken);
            CommerceSemanticOutcome outcome = CommerceOutcomes.Classify(response);
            outcomes[outcome] = outcomes.GetValueOrDefault(outcome) + 1;
            commandCount++;

            if (!command.ExpectedOutcome.Allows(outcome))
            {
                throw new InvalidOperationException(
                    $"Fixture command {command.Ordinal} ({command.HandlerName}) returned unexpected outcome {outcome}.");
            }

            if (outcome == CommerceSemanticOutcome.Committed &&
                !response.Committed.Events.Select(value => value.Type).SequenceEqual(command.ExpectedEventTypes))
            {
                throw new InvalidOperationException(
                    $"Fixture command {command.Ordinal} ({command.HandlerName}) committed an unexpected event batch.");
            }
        }

        long endingHead = (await client.GetHeadAsync(database, cancellationToken)).EventId;
        return new CommerceFixturePopulationResult(
            startingHead,
            endingHead,
            commandCount,
            new ReadOnlyDictionary<CommerceSemanticOutcome, long>(outcomes));
    }

    private static CommerceFixtureCommand Create(
        CommerceFixture fixture,
        long ordinal,
        object command,
        params string[] eventTypes)
    {
        return new CommerceFixtureCommand(
            ordinal,
            CommerceCommandIds.ForFixture(fixture.Options.Seed, ordinal),
            command.GetType().Name,
            command,
            eventTypes,
            CommerceExpectedOutcomes.FixturePopulation);
    }

    private static IReadOnlyList<CandidateEvent> ToEvents(
        CommerceFixtureCommand fixtureCommand,
        FixtureHistoryState state)
    {
        switch (fixtureCommand.Command)
        {
            case PublishProduct command:
                state.ProductPrices[command.Sku] = command.UnitPriceMinor;
                return
                [
                    Candidate(nameof(ProductPublished),
                        new ProductPublished(command.Sku, command.Name, command.UnitPriceMinor, command.Currency),
                        new EventKey("sku", command.Sku))
                ];
            case ReceiveInventory command:
                return
                [
                    Candidate(nameof(InventoryReceived),
                        new InventoryReceived(command.ReceiptId, command.Sku, command.WarehouseId, command.Quantity),
                        new EventKey("receipt", command.ReceiptId),
                        new EventKey("sku", command.Sku),
                        new EventKey("warehouse", command.WarehouseId))
                ];
            case RegisterCustomer command:
                return
                [
                    Candidate(nameof(CustomerRegistered),
                        new CustomerRegistered(command.CustomerId, command.DisplayName),
                        new EventKey("customer", command.CustomerId))
                ];
            case OpenCart command:
                return
                [
                    Candidate(nameof(CartOpened),
                        new CartOpened(command.CartId, command.CustomerId, command.Currency),
                        new EventKey("cart", command.CartId),
                        new EventKey("customer", command.CustomerId))
                ];
            case ReserveCartLine command:
            {
                long unitPrice = state.ProductPrices[command.Sku];
                long lineTotal = checked(unitPrice * command.Quantity);
                state.CartTotals[command.CartId] = (lineTotal, "USD");
                return
                [
                    Candidate(nameof(CartLineReserved),
                        new CartLineReserved(
                            command.CartId, command.LineId, command.CustomerId, command.Sku, command.WarehouseId,
                            command.Quantity, unitPrice, lineTotal, "USD"),
                        new EventKey("cart", command.CartId),
                        new EventKey("line", command.LineId),
                        new EventKey("customer", command.CustomerId),
                        new EventKey("sku", command.Sku),
                        new EventKey("warehouse", command.WarehouseId)),
                    Candidate(nameof(InventoryReserved),
                        new InventoryReserved(
                            command.Sku, command.WarehouseId, command.CartId, command.LineId, command.Quantity),
                        new EventKey("sku", command.Sku),
                        new EventKey("warehouse", command.WarehouseId),
                        new EventKey("cart", command.CartId),
                        new EventKey("line", command.LineId))
                ];
            }
            case CheckoutCart command:
            {
                (long total, string currency) = state.CartTotals[command.CartId];
                state.OrderTotals[command.OrderId] = (total, currency);
                return
                [
                    Candidate(nameof(CartCheckedOut),
                        new CartCheckedOut(command.CartId, command.OrderId, command.CustomerId, 1, total, currency),
                        new EventKey("cart", command.CartId),
                        new EventKey("order", command.OrderId),
                        new EventKey("customer", command.CustomerId)),
                    Candidate(nameof(OrderPlaced),
                        new OrderPlaced(command.OrderId, command.CartId, command.CustomerId, 1, total, currency),
                        new EventKey("order", command.OrderId),
                        new EventKey("cart", command.CartId),
                        new EventKey("customer", command.CustomerId))
                ];
            }
            case AuthorizePayment command:
            {
                (long total, string currency) = state.OrderTotals[command.OrderId];
                return
                [
                    Candidate(nameof(PaymentAuthorized),
                        new PaymentAuthorized(
                            command.PaymentId, command.OrderId, total, currency,
                            $"authorization-{command.PaymentId}"),
                        new EventKey("payment", command.PaymentId),
                        new EventKey("order", command.OrderId))
                ];
            }
            case CreateShipment command:
                return
                [
                    Candidate(nameof(ShipmentCreated),
                        new ShipmentCreated(
                            command.ShipmentId, command.OrderId, command.CustomerId, command.Carrier),
                        new EventKey("shipment", command.ShipmentId),
                        new EventKey("order", command.OrderId),
                        new EventKey("customer", command.CustomerId))
                ];
            case ShipOrder command:
                return
                [
                    Candidate(nameof(OrderShipped),
                        new OrderShipped(
                            command.ShipmentId, command.OrderId, command.CustomerId, command.TrackingCode),
                        new EventKey("shipment", command.ShipmentId),
                        new EventKey("order", command.OrderId),
                        new EventKey("customer", command.CustomerId))
                ];
            case DeliverOrder command:
                return
                [
                    Candidate(nameof(OrderDelivered),
                        new OrderDelivered(command.ShipmentId, command.OrderId, command.CustomerId),
                        new EventKey("shipment", command.ShipmentId),
                        new EventKey("order", command.OrderId),
                        new EventKey("customer", command.CustomerId))
                ];
            case AdjustInventory command:
                return
                [
                    Candidate(nameof(InventoryAdjusted),
                        new InventoryAdjusted(
                            command.AdjustmentId, command.Sku, command.WarehouseId, command.QuantityDelta,
                            command.Reason),
                        new EventKey("adjustment", command.AdjustmentId),
                        new EventKey("sku", command.Sku),
                        new EventKey("warehouse", command.WarehouseId))
                ];
            default:
                throw new InvalidOperationException(
                    $"Unsupported fixture command type '{fixtureCommand.Command.GetType().Name}'.");
        }
    }

    private static CandidateEvent Candidate(string eventType, object payload, params EventKey[] keys)
    {
        return new CandidateEvent(eventType, JsonSerializer.SerializeToElement(payload, payload.GetType()), keys);
    }

    private sealed class FixtureHistoryState
    {
        public Dictionary<string, long> ProductPrices { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, (long Total, string Currency)> CartTotals { get; } =
            new(StringComparer.Ordinal);

        public Dictionary<string, (long Total, string Currency)> OrderTotals { get; } =
            new(StringComparer.Ordinal);
    }
}
