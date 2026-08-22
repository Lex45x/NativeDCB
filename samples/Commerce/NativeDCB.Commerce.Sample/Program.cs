using System.Globalization;
using System.Text.Json;

using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Events;
using NativeDCB.Commerce.Models;
using NativeDCB.Model.Events;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;

namespace NativeDCB.Commerce.Sample;

internal static class Program
{
    private const string DefaultAddress = "http://localhost:5010";
    private const string DefaultDatabase = "commerce-sample";
    private const string Warehouse = "sample-warehouse";

    public static async Task<int> Main(string[] args)
    {
        string mode = args.ElementAtOrDefault(0) ?? "run";
        string address = args.ElementAtOrDefault(1) ?? DefaultAddress;
        string database = args.ElementAtOrDefault(2) ?? DefaultDatabase;
        if (args.Length > 3 || mode is not ("seed" or "run" or "contention" or "remote-payment" or "recovery"))
        {
            Console.Error.WriteLine(
                "Usage: NativeDCB.Commerce.Sample [seed|run|contention|remote-payment|recovery] [address] [database]");
            return 2;
        }

        using CancellationTokenSource shutdown = new();
        ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            shutdown.Cancel();
        };
        Console.CancelKeyPress += cancelHandler;

        try
        {
            using NativeDcbClient client = new(address);
            await CommerceSeeder.SeedAsync(client, database, shutdown.Token);
            Console.WriteLine($"Catalog ready in database '{database}'.");

            switch (mode)
            {
                case "seed":
                    return 0;
                case "run":
                    await RunLifecycleAsync(client, database, shutdown.Token);
                    return 0;
                case "contention":
                    await RunContentionAsync(client, database, shutdown.Token);
                    return 0;
                case "remote-payment":
                    await RunRemotePaymentAsync(client, database, shutdown.Token);
                    return 0;
                case "recovery":
                    await RunRecoveryAsync(client, database, shutdown.Token);
                    return 0;
                default:
                    return 2;
            }
        }
        finally
        {
            Console.CancelKeyPress -= cancelHandler;
        }
    }

    private static async Task RunLifecycleAsync(
        NativeDcbClient client,
        string database,
        CancellationToken cancellationToken)
    {
        const string sku = "sample-run-sku";
        const string customer = "sample-run-customer";
        const string cart = "sample-run-cart";
        const string line = "sample-run-line";
        const string order = "sample-run-order";
        const string coupon = "SAMPLE-RUN-10";
        const string payment = "sample-run-payment";
        const string shipment = "sample-run-shipment";
        const string returnId = "sample-run-return";

        Console.WriteLine("\nComplete commerce lifecycle");
        await ExecuteAndPrintAsync(client, database, "Product published",
            new PublishProduct(sku, "Native DCB Mug", 2_500, "USD"), 1001, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Inventory received",
            new ReceiveInventory("sample-run-receipt", sku, Warehouse, 10), 1002, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Customer registered",
            new RegisterCustomer(customer, "Sample Customer"), 1003, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Cart opened",
            new OpenCart(cart, customer, "USD"), 1004, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Inventory reserved for cart line",
            new ReserveCartLine(cart, line, customer, sku, Warehouse, 2), 1005, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Cart checked out and order placed",
            new CheckoutCart(cart, order, customer), 1006, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Coupon defined",
            new DefineCoupon(coupon, 500, "USD", 10, 1), 1007, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Coupon redeemed and discount applied",
            new RedeemCoupon(coupon, customer, order), 1008, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Payment authorized locally",
            new AuthorizePayment(payment, order), 1009, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Shipment created",
            new CreateShipment(shipment, order, customer, "Sample Carrier"), 1010, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Order shipped",
            new ShipOrder(shipment, order, customer, "TRACK-SAMPLE-001"), 1011, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Order delivered",
            new DeliverOrder(shipment, order, customer), 1012, cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Return requested",
            new RequestReturn(returnId, order, line, customer, sku, 2, "Changed mind"),
            1013,
            cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Return received and inventory restocked",
            new ReceiveReturn(returnId, order, line, customer, sku, Warehouse, 2),
            1014,
            cancellationToken);
        await ExecuteAndPrintAsync(client, database, "Payment refunded locally",
            new RefundPayment("sample-run-refund", payment, order, returnId), 1015, cancellationToken);
    }

    private static async Task RunContentionAsync(
        NativeDcbClient client,
        string database,
        CancellationToken cancellationToken)
    {
        Console.WriteLine("\nInventory contention");
        const string stockSku = "sample-contention-stock-sku";
        await ExecuteQuietlyAsync(client, database,
            new PublishProduct(stockSku, "Last Item", 1_000, "USD"), 2101, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new ReceiveInventory("sample-contention-stock-receipt", stockSku, Warehouse, 1),
            2102,
            cancellationToken);
        await SetupOpenCartAsync(client, database, "sample-stock-customer-a", "sample-stock-cart-a", 2103,
            cancellationToken);
        await SetupOpenCartAsync(client, database, "sample-stock-customer-b", "sample-stock-cart-b", 2105,
            cancellationToken);

        ExecuteHandlerResponse[] stockResults = await Task.WhenAll(
            ExecuteQuietlyAsync(client, database,
                new ReserveCartLine("sample-stock-cart-a", "sample-stock-line-a", "sample-stock-customer-a",
                    stockSku, Warehouse, 1),
                2111,
                cancellationToken),
            ExecuteQuietlyAsync(client, database,
                new ReserveCartLine("sample-stock-cart-b", "sample-stock-line-b", "sample-stock-customer-b",
                    stockSku, Warehouse, 1),
                2112,
                cancellationToken));
        long stockReceived = await SumEventPropertyAsync(client, database, "InventoryReceived", "Quantity",
            [new EventKey("sku", stockSku), new EventKey("warehouse", Warehouse)], cancellationToken);
        long stockReserved = await SumEventPropertyAsync(client, database, "InventoryReserved", "Quantity",
            [new EventKey("sku", stockSku), new EventKey("warehouse", Warehouse)], cancellationToken);
        Console.WriteLine($"Race outcomes: {JoinOutcomes(stockResults)}");
        Console.WriteLine(
            $"Invariant: received={stockReceived}, reserved={stockReserved}, available={stockReceived - stockReserved}, nonnegative={stockReceived - stockReserved >= 0}.");

        Console.WriteLine("\nReservation versus checkout ordering");
        const string orderingSku = "sample-contention-ordering-sku";
        const string orderingCustomer = "sample-ordering-customer";
        const string orderingCart = "sample-ordering-cart";
        const string orderingOrder = "sample-ordering-order";
        await ExecuteQuietlyAsync(client, database,
            new PublishProduct(orderingSku, "Ordering Item", 700, "USD"), 2201, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new ReceiveInventory("sample-ordering-receipt", orderingSku, Warehouse, 2), 2202, cancellationToken);
        await SetupOpenCartAsync(client, database, orderingCustomer, orderingCart, 2203, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new ReserveCartLine(orderingCart, "sample-ordering-line-1", orderingCustomer, orderingSku, Warehouse, 1),
            2205,
            cancellationToken);

        ExecuteHandlerResponse[] orderingResults = await Task.WhenAll(
            ExecuteQuietlyAsync(client, database,
                new ReserveCartLine(orderingCart, "sample-ordering-line-2", orderingCustomer, orderingSku,
                    Warehouse, 1),
                2211,
                cancellationToken),
            ExecuteQuietlyAsync(client, database,
                new CheckoutCart(orderingCart, orderingOrder, orderingCustomer), 2212, cancellationToken));
        long reservedLines = await CountEventsAsync(client, database, "CartLineReserved",
            [new EventKey("cart", orderingCart)], cancellationToken);
        long orderLineCount = await SingleEventPropertyAsync(client, database, "OrderPlaced", "LineCount",
            [new EventKey("order", orderingOrder)], cancellationToken);
        Console.WriteLine($"Race outcomes: {JoinOutcomes(orderingResults)}");
        Console.WriteLine(
            $"Invariant: reserved lines={reservedLines}, order line count={orderLineCount}, checkout reflects committed ordering={reservedLines == orderLineCount}.");

        Console.WriteLine("\nCoupon contention");
        const string couponSku = "sample-contention-coupon-sku";
        const string couponCode = "SAMPLE-ONE-USE";
        await ExecuteQuietlyAsync(client, database,
            new PublishProduct(couponSku, "Coupon Item", 1_000, "USD"), 2301, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new ReceiveInventory("sample-coupon-receipt", couponSku, Warehouse, 2), 2302, cancellationToken);
        await SetupOrderAsync(client, database, couponSku, "sample-coupon-customer-a", "sample-coupon-cart-a",
            "sample-coupon-line-a", "sample-coupon-order-a", 2310, cancellationToken);
        await SetupOrderAsync(client, database, couponSku, "sample-coupon-customer-b", "sample-coupon-cart-b",
            "sample-coupon-line-b", "sample-coupon-order-b", 2320, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new DefineCoupon(couponCode, 100, "USD", 1, 1), 2330, cancellationToken);

        ExecuteHandlerResponse[] couponResults = await Task.WhenAll(
            ExecuteQuietlyAsync(client, database,
                new RedeemCoupon(couponCode, "sample-coupon-customer-a", "sample-coupon-order-a"),
                2331,
                cancellationToken),
            ExecuteQuietlyAsync(client, database,
                new RedeemCoupon(couponCode, "sample-coupon-customer-b", "sample-coupon-order-b"),
                2332,
                cancellationToken));
        long redemptions = await CountEventsAsync(client, database, "CouponRedeemed",
            [new EventKey("coupon", couponCode)], cancellationToken);
        Console.WriteLine($"Race outcomes: {JoinOutcomes(couponResults)}");
        Console.WriteLine($"Invariant: global limit=1, committed redemptions={redemptions}, limit respected={redemptions <= 1}.");

        Console.WriteLine("\nDuplicate command reconciliation");
        const string duplicateSku = "sample-contention-duplicate-sku";
        const string duplicateReceipt = "sample-duplicate-receipt";
        await ExecuteQuietlyAsync(client, database,
            new PublishProduct(duplicateSku, "Duplicate Item", 300, "USD"), 2401, cancellationToken);
        Guid duplicateCommandId = CommandId(2402);
        ReceiveInventory duplicateCommand = new(duplicateReceipt, duplicateSku, Warehouse, 3);
        ExecuteHandlerResponse[] duplicateResults = await Task.WhenAll(
            client.ExecuteHandlerAsync(database, nameof(ReceiveInventory), duplicateCommand, duplicateCommandId,
                cancellationToken),
            client.ExecuteHandlerAsync(database, nameof(ReceiveInventory), duplicateCommand, duplicateCommandId,
                cancellationToken));
        GetEventsByCommandIdResponse reconciled = await client.GetEventsByCommandIdAsync(
            database, duplicateCommandId, cancellationToken);
        long receiptEvents = await CountEventsAsync(client, database, "InventoryReceived",
            [new EventKey("receipt", duplicateReceipt)], cancellationToken);
        Console.WriteLine($"Race outcomes: {JoinOutcomes(duplicateResults)}");
        Console.WriteLine(
            $"Invariant: persisted receipt events={receiptEvents}, reconciliation batch size={reconciled.Events.Count}, exactly one batch={receiptEvents == 1 && reconciled.Events.Count == 1}.");
    }

    private static async Task RunRemotePaymentAsync(
        NativeDcbClient client,
        string database,
        CancellationToken cancellationToken)
    {
        const string sku = "sample-remote-sku";
        const string customer = "sample-remote-customer";
        const string order = "sample-remote-order";
        await ExecuteQuietlyAsync(client, database,
            new PublishProduct(sku, "Remote Payment Item", 3_000, "USD"), 3001, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new ReceiveInventory("sample-remote-receipt", sku, Warehouse, 1), 3002, cancellationToken);
        await SetupOrderAsync(client, database, sku, customer, "sample-remote-cart", "sample-remote-line", order,
            3010, cancellationToken);

        PreparedDecision<PaymentPreparationModel> prepared = await client.PrepareDecisionAsync<
            AuthorizePayment, PaymentPreparationModel>(
            database,
            nameof(AuthorizePayment),
            new AuthorizePayment("sample-remote-payment", order),
            CommandId(3020),
            cancellationToken);
        PreparedDecision<PaymentPreparationModel> staleCandidate = await client.PrepareDecisionAsync<
            AuthorizePayment, PaymentPreparationModel>(
            database,
            nameof(AuthorizePayment),
            new AuthorizePayment("sample-remote-stale-payment", order),
            CommandId(3021),
            cancellationToken);
        Console.WriteLine($"Prepared payment: {prepared.OutcomeCase}; stale candidate: {staleCandidate.OutcomeCase}.");
        if (!prepared.IsPrepared || !staleCandidate.IsPrepared)
        {
            Console.WriteLine("Use a fresh database to run the deterministic remote-payment scenario again.");
            return;
        }

        PaymentAuthorized paymentEvent = CreatePaymentEvent(
            "sample-remote-payment", order, prepared.Model, "sample-gateway-authorization");
        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            database,
            prepared.ModelSignature,
            [new ProposedDecisionEvent(nameof(PaymentAuthorized), paymentEvent)],
            cancellationToken);
        Console.WriteLine(
            $"Trusted worker proposed {paymentEvent.AmountMinor} {paymentEvent.Currency}; completion: {completed.OutcomeCase}.");

        PaymentAuthorized staleEvent = CreatePaymentEvent(
            "sample-remote-stale-payment", order, staleCandidate.Model, "sample-gateway-stale-authorization");
        CompleteDecisionResponse stale = await client.CompleteDecisionAsync(
            database,
            staleCandidate.ModelSignature,
            [new ProposedDecisionEvent(nameof(PaymentAuthorized), staleEvent)],
            cancellationToken);
        Console.WriteLine(
            $"The first authorization changed matching order state; older capability completion: {stale.OutcomeCase} (expected Stale).");
    }

    private static async Task RunRecoveryAsync(
        NativeDcbClient client,
        string database,
        CancellationToken cancellationToken)
    {
        Guid commandId = CommandId(4001);
        PublishProduct command = new("sample-recovery-sku", "Recovery Item", 1_200, "USD");
        ExecuteHandlerResponse executed = await client.ExecuteHandlerAsync(
            database, nameof(PublishProduct), command, commandId, cancellationToken);
        ExecuteHandlerResponse replayed = await client.ExecuteHandlerAsync(
            database, nameof(PublishProduct), command, commandId, cancellationToken);
        GetEventsByCommandIdResponse reconciled = await client.GetEventsByCommandIdAsync(
            database, commandId, cancellationToken);
        string commandIdText = commandId.ToString("D", CultureInfo.InvariantCulture);
        bool verified = reconciled.Events.Count == 1 &&
                        reconciled.Events[0].Type == nameof(ProductPublished) &&
                        reconciled.Events[0].CommandId == commandIdText;

        Console.WriteLine($"Execute outcome: {executed.OutcomeCase}.");
        Console.WriteLine($"Replay outcome: {replayed.OutcomeCase}.");
        Console.WriteLine(
            $"GetEventsByCommandId returned {reconciled.Events.Count} event(s); original ProductPublished batch verified={verified}.");
    }

    private static PaymentAuthorized CreatePaymentEvent(
        string paymentId,
        string orderId,
        PaymentPreparationModel model,
        string gatewayReference)
    {
        string currency = model.Currency ?? throw new InvalidOperationException("Prepared payment has no currency.");
        if (!model.OrderExists || model.IsCancelled || model.IsAuthorized || model.PayableAmountMinor <= 0)
        {
            throw new InvalidOperationException("The prepared payment model is not eligible for authorization.");
        }

        return new PaymentAuthorized(paymentId, orderId, model.PayableAmountMinor, currency, gatewayReference);
    }

    private static async Task SetupOpenCartAsync(
        NativeDcbClient client,
        string database,
        string customerId,
        string cartId,
        int commandOrdinal,
        CancellationToken cancellationToken)
    {
        await ExecuteQuietlyAsync(client, database,
            new RegisterCustomer(customerId, $"Customer {customerId}"), commandOrdinal, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new OpenCart(cartId, customerId, "USD"), commandOrdinal + 1, cancellationToken);
    }

    private static async Task SetupOrderAsync(
        NativeDcbClient client,
        string database,
        string sku,
        string customerId,
        string cartId,
        string lineId,
        string orderId,
        int commandOrdinal,
        CancellationToken cancellationToken)
    {
        await SetupOpenCartAsync(client, database, customerId, cartId, commandOrdinal, cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new ReserveCartLine(cartId, lineId, customerId, sku, Warehouse, 1),
            commandOrdinal + 2,
            cancellationToken);
        await ExecuteQuietlyAsync(client, database,
            new CheckoutCart(cartId, orderId, customerId), commandOrdinal + 3, cancellationToken);
    }

    private static async Task ExecuteAndPrintAsync<TCommand>(
        NativeDcbClient client,
        string database,
        string semanticOutcome,
        TCommand command,
        int commandOrdinal,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        ExecuteHandlerResponse response = await ExecuteQuietlyAsync(
            client, database, command, commandOrdinal, cancellationToken);
        Console.WriteLine($"{semanticOutcome}: {Describe(response)}");
    }

    private static Task<ExecuteHandlerResponse> ExecuteQuietlyAsync<TCommand>(
        NativeDcbClient client,
        string database,
        TCommand command,
        int commandOrdinal,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        return client.ExecuteHandlerAsync(
            database, typeof(TCommand).Name, command, CommandId(commandOrdinal), cancellationToken);
    }

    private static string Describe(ExecuteHandlerResponse response)
    {
        return response.OutcomeCase switch
        {
            ExecuteHandlerResponse.OutcomeOneofCase.Committed =>
                $"Committed [{string.Join(", ", response.Committed.Events.Select(value => value.Type))}]",
            ExecuteHandlerResponse.OutcomeOneofCase.AlreadyCommitted =>
                $"AlreadyCommitted [events {response.AlreadyCommitted.FirstEventId}-{response.AlreadyCommitted.LastEventId}]",
            ExecuteHandlerResponse.OutcomeOneofCase.Rejected =>
                $"Rejected [{response.Rejected.Code}: {response.Rejected.Message}]",
            ExecuteHandlerResponse.OutcomeOneofCase.Failed =>
                $"Failed [{response.Failed.Error.Code}: {response.Failed.Error.Message}]",
            _ => response.OutcomeCase.ToString()
        };
    }

    private static string JoinOutcomes(IEnumerable<ExecuteHandlerResponse> responses)
    {
        return string.Join(", ", responses.Select(response => response.OutcomeCase.ToString()));
    }

    private static async Task<long> CountEventsAsync(
        NativeDcbClient client,
        string database,
        string eventType,
        IReadOnlyCollection<EventKey> keys,
        CancellationToken cancellationToken)
    {
        long count = 0;
        await foreach (SequencedEvent _ in client.ReadEventsByTypeAndKeysAsync(
                           database, eventType, keys, cancellationToken: cancellationToken))
        {
            count++;
        }

        return count;
    }

    private static async Task<long> SumEventPropertyAsync(
        NativeDcbClient client,
        string database,
        string eventType,
        string propertyName,
        IReadOnlyCollection<EventKey> keys,
        CancellationToken cancellationToken)
    {
        long total = 0;
        await foreach (SequencedEvent value in client.ReadEventsByTypeAndKeysAsync(
                           database, eventType, keys, cancellationToken: cancellationToken))
        {
            total += GetInt64(value.Data, propertyName);
        }

        return total;
    }

    private static async Task<long> SingleEventPropertyAsync(
        NativeDcbClient client,
        string database,
        string eventType,
        string propertyName,
        IReadOnlyCollection<EventKey> keys,
        CancellationToken cancellationToken)
    {
        long? result = null;
        await foreach (SequencedEvent value in client.ReadEventsByTypeAndKeysAsync(
                           database, eventType, keys, cancellationToken: cancellationToken))
        {
            if (result.HasValue)
            {
                throw new InvalidOperationException($"Expected one {eventType} event but found more than one.");
            }

            result = GetInt64(value.Data, propertyName);
        }

        return result ?? throw new InvalidOperationException($"Expected one {eventType} event but found none.");
    }

    private static long GetInt64(JsonElement data, string propertyName)
    {
        if (data.TryGetProperty(propertyName, out JsonElement value))
        {
            return value.GetInt64();
        }

        string camelCaseName = JsonNamingPolicy.CamelCase.ConvertName(propertyName);
        if (data.TryGetProperty(camelCaseName, out value))
        {
            return value.GetInt64();
        }

        throw new InvalidOperationException($"Event data does not contain '{propertyName}'.");
    }

    private static Guid CommandId(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinal);
        string suffix = ordinal.ToString("D12", CultureInfo.InvariantCulture);
        return Guid.ParseExact($"00000000-0000-0000-0000-{suffix}", "D");
    }
}
