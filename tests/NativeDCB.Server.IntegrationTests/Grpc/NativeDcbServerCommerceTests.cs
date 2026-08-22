using System.Text.Json;

using Grpc.Net.Client;

using NativeDCB.Commerce;
using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Events;
using NativeDCB.Commerce.Fixtures;
using NativeDCB.Commerce.Models;
using NativeDCB.Commerce.Verification;
using NativeDCB.Commerce.Workloads;
using NativeDCB.Generated;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Decisions.Evaluation;
using NativeDCB.Model.Decisions.Expressions;
using NativeDCB.Model.Events;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Schemas;

namespace NativeDCB.Server.IntegrationTests.Grpc;

public sealed partial class NativeDcbServerTests
{
    [Fact]
    public async Task Commerce_embedded_ndl_and_sdk_variants_preserve_documented_boundaries()
    {
        string source = await CommerceNdl.LoadAsync();
        var compilation = NativeDCB.Ndl.Ndl.Compile(source);
        Assert.False(compilation.HasErrors,
            string.Join(Environment.NewLine, compilation.Diagnostics.Select(value => value.Message)));

        string[] expectedNames =
        [
            "PublishProduct",
            "ChangeProductPrice",
            "DiscontinueProduct",
            "ReceiveInventory",
            "AdjustInventory",
            "RegisterCustomer",
            "OpenCart",
            "ReserveCartLine",
            "RemoveCartLine",
            "CheckoutCart",
            "DefineCoupon",
            "RedeemCoupon",
            "AuthorizePayment",
            "RecordPaymentDeclined",
            "CancelOrder",
            "CreateShipment",
            "ShipOrder",
            "DeliverOrder",
            "RequestReturn",
            "ReceiveReturn",
            "RefundPayment"
        ];
        Assert.Equal(expectedNames, compilation.Plans.Select(value => value.Name));

        IReadOnlyDictionary<string, DecisionPlan> plans = compilation.Plans.ToDictionary(value => value.Name);
        AssertCommerceSdkBoundary(
            plans[nameof(PublishProduct)],
            CommerceDecisionDefinitions.PublishProductSdk(),
            "PublishProduct validation failed");
        AssertCommerceSdkBoundary(
            plans[nameof(ReserveCartLine)],
            CommerceDecisionDefinitions.ReserveCartLineSdk(),
            "ReserveCartLine validation failed");
        AssertCommerceSdkBoundary(
            plans[nameof(CheckoutCart)],
            CommerceDecisionDefinitions.CheckoutCartSdk(),
            "CheckoutCart validation failed");
    }

    [Fact]
    public void Commerce_fixture_profiles_and_key_selection_are_deterministic()
    {
        (CommerceFixtureProfile Profile, int Products, int Warehouses, int Customers, int Carts, long Events)[]
            profiles =
            [
                (CommerceFixtureProfile.Tiny, 10, 2, 20, 10, 100),
                (CommerceFixtureProfile.Small, 100, 4, 1_000, 250, 10_000),
                (CommerceFixtureProfile.Medium, 1_000, 8, 10_000, 2_000, 100_000)
            ];

        foreach (var expected in profiles)
        {
            CommerceFixture fixture = new(CommerceFixtureOptions.ForProfile(expected.Profile, seed: 42));
            Assert.Equal(expected.Products, fixture.Skus.Length);
            Assert.Equal(expected.Warehouses, fixture.WarehouseIds.Length);
            Assert.Equal(expected.Customers, fixture.CustomerIds.Length);
            Assert.Equal(expected.Carts, fixture.CartIds.Length);
            Assert.Equal(expected.Events, fixture.Options.PreloadedEventCount);
            Assert.Equal("sku-00000001", fixture.Skus[0]);
            Assert.Equal($"sku-{expected.Products:D8}", fixture.Skus[^1]);
        }

        CommerceFixtureOptions options = CommerceFixtureOptions.Custom(
            seed: 8675309,
            productCount: 17,
            warehouseCount: 5,
            customerCount: 19,
            openCartCount: 11,
            preloadedEventCount: 250);
        CommerceFixture first = new(options);
        CommerceFixture second = new(options);
        foreach (CommerceKeyDistribution distribution in Enum.GetValues<CommerceKeyDistribution>())
        {
            Assert.Equal(
                Enumerable.Range(start: 0, count: 64).Select(sample => first.SelectSku(sample, distribution)),
                Enumerable.Range(start: 0, count: 64).Select(sample => second.SelectSku(sample, distribution)));
            Assert.Equal(
                Enumerable.Range(start: 0, count: 64).Select(sample => first.SelectWarehouseId(sample, distribution)),
                Enumerable.Range(start: 0, count: 64).Select(sample => second.SelectWarehouseId(sample, distribution)));
            Assert.Equal(
                Enumerable.Range(start: 0, count: 64).Select(sample => first.SelectCustomerId(sample, distribution)),
                Enumerable.Range(start: 0, count: 64).Select(sample => second.SelectCustomerId(sample, distribution)));
            Assert.Equal(
                Enumerable.Range(start: 0, count: 64).Select(sample => first.SelectCartId(sample, distribution)),
                Enumerable.Range(start: 0, count: 64).Select(sample => second.SelectCartId(sample, distribution)));
        }

        Assert.All(Enumerable.Range(start: 0, count: 64), sample =>
        {
            Assert.Equal("sku-00000001", first.SelectSku(sample, CommerceKeyDistribution.HotKey));
            Assert.Equal("warehouse-00000001", first.SelectWarehouseId(sample, CommerceKeyDistribution.HotKey));
            Assert.Equal("customer-00000001", first.SelectCustomerId(sample, CommerceKeyDistribution.HotKey));
            Assert.Equal("cart-00000001", first.SelectCartId(sample, CommerceKeyDistribution.HotKey));
        });

        CommerceFixture differentSeed = new(CommerceFixtureOptions.Custom(
            seed: options.Seed + 1,
            productCount: options.ProductCount,
            warehouseCount: options.WarehouseCount,
            customerCount: options.CustomerCount,
            openCartCount: options.OpenCartCount,
            preloadedEventCount: options.PreloadedEventCount,
            zipfianExponent: options.ZipfianExponent));
        Assert.False(Enumerable.Range(start: 0, count: 64)
            .Select(sample => first.SelectSku(sample, CommerceKeyDistribution.Uniform))
            .SequenceEqual(Enumerable.Range(start: 0, count: 64)
                .Select(sample => differentSeed.SelectSku(sample, CommerceKeyDistribution.Uniform))));
        Assert.False(Enumerable.Range(start: 0, count: 64)
            .Select(sample => first.SelectSku(sample, CommerceKeyDistribution.Zipfian))
            .SequenceEqual(Enumerable.Range(start: 0, count: 64)
                .Select(sample => differentSeed.SelectSku(sample, CommerceKeyDistribution.Zipfian))));
    }

    [Fact]
    public void Commerce_workload_contracts_are_deterministic_queryable_and_framework_independent()
    {
        CommerceFixture fixture = new(CommerceFixtureOptions.Custom(
            seed: 104729,
            productCount: 3,
            warehouseCount: 2,
            customerCount: 4,
            openCartCount: 2,
            preloadedEventCount: 24));

        CommerceFixtureCommand[] firstCommands = CommerceFixturePopulation.CreateCommands(fixture).ToArray();
        CommerceFixtureCommand[] secondCommands = CommerceFixturePopulation.CreateCommands(fixture).ToArray();
        Assert.Equal(expected: 22, firstCommands.Length);
        Assert.Equal(firstCommands.Select(value => value.CommandId), secondCommands.Select(value => value.CommandId));
        Assert.Equal(22, firstCommands.Select(value => value.CommandId).Distinct().Count());
        Assert.All(firstCommands, value =>
            Assert.True(value.ExpectedOutcome.Allows(CommerceSemanticOutcome.Committed)));

        Assert.Equal(
            Guid.Parse("0000c001-0000-0000-0000-00000000000f"),
            CommerceCommandIds.Create(0xC001, 15));
        CommerceWorkloadSlot firstSlot = CommerceWorkloadSlot.Create(
            fixture, commandScope: 0xB001, ordinal: 7, CommerceKeyDistribution.HotKey);
        CommerceWorkloadSlot secondSlot = CommerceWorkloadSlot.Create(
            fixture, commandScope: 0xB001, ordinal: 7, CommerceKeyDistribution.HotKey);
        Assert.Equal(firstSlot, secondSlot);
        Assert.Equal("sku-00000001", firstSlot.Sku);
        Assert.Equal("warehouse-00000001", firstSlot.WarehouseId);
        Assert.Equal("workload-cart-000000000008", firstSlot.CartId);
        Assert.Equal(firstSlot.CommandId(1), secondSlot.CommandId(1));
        Assert.NotEqual(firstSlot.CommandId(1), firstSlot.CommandId(2));

        IReadOnlyList<SequencedEvent> history = CommerceFixturePopulation.CreateHistory(fixture);
        Assert.Equal(expected: 24, history.Count);
        Assert.Equal(Enumerable.Range(1, 24).Select(value => (long)value), history.Select(value => value.EventId));
        Assert.Equal(firstCommands.SelectMany(value =>
            Enumerable.Repeat(value.CommandId, value.ExpectedEventTypes.Count)), history.Select(value => value.CommandId));
        SequencedEvent receipt = history.First(value => value.Type == nameof(InventoryReceived));
        string receiptSku = receipt.Data.GetProperty("Sku").GetString()!;
        string receiptWarehouse = receipt.Data.GetProperty("WarehouseId").GetString()!;
        Assert.True(CommerceQueries.Inventory(receiptSku, receiptWarehouse).Matches(receipt));
        Assert.False(CommerceQueries.Inventory("another-sku", receiptWarehouse).Matches(receipt));
        SequencedEvent order = history.Single(value => value.Type == nameof(OrderPlaced));
        Assert.True(CommerceQueries.Order(order.Data.GetProperty("OrderId").GetString()!).Matches(order));
        Assert.Equal(
            [nameof(OrderPlaced), nameof(PaymentAuthorized), nameof(OrderShipped), nameof(OrderDelivered),
                nameof(PaymentRefunded)],
            Assert.Single(CommerceQueries.OrderLifecycleSubscription().Items).EventTypes);
        Assert.Empty(CommerceQueries.FullAudit().Items);

        CommerceVerificationResult valid = CommerceCorrectness.VerifyHistory(history);
        Assert.True(valid.IsValid, string.Join(Environment.NewLine, valid.Issues.Select(value => value.Message)));
        Assert.Equal(expected: 24, valid.Summary.EventCount);
        Assert.Equal(expected: 22, valid.Summary.CommandCount);
        Assert.Equal(expected: 0, valid.Summary.MinimumObservedInventory);

        InventoryReserved invalidReservation = new(receiptSku, receiptWarehouse, "invalid-cart", "invalid-line",
            1);
        SequencedEvent invalidEvent = receipt with
        {
            Type = nameof(InventoryReserved),
            Data = JsonSerializer.SerializeToElement(invalidReservation),
            Keys =
            [
                new EventKey("sku", receiptSku),
                new EventKey("warehouse", receiptWarehouse),
                new EventKey("cart", invalidReservation.CartId),
                new EventKey("line", invalidReservation.LineId)
            ]
        };
        SequencedEvent[] invalidHistory = history.ToArray();
        invalidHistory[Array.IndexOf(invalidHistory, receipt)] = invalidEvent;
        CommerceVerificationResult invalid = CommerceCorrectness.VerifyHistory(invalidHistory);
        Assert.False(invalid.IsValid);
        Assert.Contains(invalid.Issues, value => value.Code == "NegativeInventory");
        Assert.Throws<InvalidOperationException>(invalid.ThrowIfInvalid);
    }

    [Fact]
    public async Task Commerce_fixture_population_is_exact_reconcilable_and_verifiable()
    {
        const string database = "commerce-fixture-population";
        CommerceFixture fixture = new(CommerceFixtureOptions.Custom(
            seed: 65537,
            productCount: 3,
            warehouseCount: 2,
            customerCount: 4,
            openCartCount: 2,
            preloadedEventCount: 24));
        using NativeDcbClient client = CreateCommerceClient();

        await CommerceSeeder.SeedAsync(client, database, CancellationToken.None);
        Assert.Equal(expected: 0, (await client.GetHeadAsync(database)).EventId);

        CommerceFixturePopulationResult populated = await CommerceFixturePopulation.PopulateAsync(
            client, database, fixture);
        Assert.Equal(expected: 0, populated.StartingHead);
        Assert.Equal(expected: 24, populated.EndingHead);
        Assert.Equal(expected: 22, populated.CommandCount);
        Assert.Equal(expected: 24, populated.AppendedEventCount);
        Assert.Equal(expected: 22, populated.Outcomes[CommerceSemanticOutcome.Committed]);

        CommerceFixturePopulationResult reconciled = await CommerceFixturePopulation.PopulateAsync(
            client, database, fixture);
        Assert.Equal(expected: 24, reconciled.StartingHead);
        Assert.Equal(expected: 24, reconciled.EndingHead);
        Assert.Equal(expected: 0, reconciled.AppendedEventCount);
        Assert.Equal(expected: 22, reconciled.Outcomes[CommerceSemanticOutcome.Reconciled]);

        IReadOnlyList<SequencedEvent> events = await ReadCommerceEventsAsync(client, database);
        CommerceVerificationResult verification = CommerceCorrectness.VerifyHistory(events);
        Assert.True(verification.IsValid,
            string.Join(Environment.NewLine, verification.Issues.Select(value => value.Message)));
        Assert.Equal(expected: 24, verification.Summary.EventCount);
        Assert.Equal(expected: 22, verification.Summary.CommandCount);
    }

    [Fact]
    public async Task Commerce_seeding_is_idempotent_and_complete_lifecycle_commits_expected_events()
    {
        const string database = "commerce-lifecycle";
        const string prefix = "lifecycle";
        const string warehouse = $"{prefix}-warehouse";
        const string sku = $"{prefix}-sku";
        const string customer = $"{prefix}-customer";
        const string cart = $"{prefix}-cart";
        const string line = $"{prefix}-line";
        const string order = $"{prefix}-order";
        const string coupon = "LIFECYCLE-500";
        const string payment = $"{prefix}-payment";
        const string shipment = $"{prefix}-shipment";
        const string returnId = $"{prefix}-return";

        using NativeDcbClient client = CreateCommerceClient();
        await CommerceSeeder.SeedAsync(client, database, CancellationToken.None);
        ListHandlersResponse firstHandlers = await client.ListHandlersAsync(database);
        ListSchemasResponse firstSchemas = await client.ListSchemasAsync(database);
        IReadOnlyList<SchemaDescriptor> generatedSchemas = NativeDcbGeneratedSchemas.Create();
        await CommerceSeeder.SeedAsync(client, database, CancellationToken.None);
        ListHandlersResponse secondHandlers = await client.ListHandlersAsync(database);
        ListSchemasResponse secondSchemas = await client.ListSchemasAsync(database);

        Assert.Equal(expected: 24, firstHandlers.Handlers.Count);
        Assert.Equal(expected: 47, firstSchemas.Schemas.Count);
        Assert.Equal(
            generatedSchemas
                .Select(value => (
                    value.Name,
                    Kind: value.SchemaType == SchemaType.Event ? SchemaKind.Event : SchemaKind.Command))
                .OrderBy(value => value.Name, StringComparer.Ordinal)
                .ThenBy(value => value.Kind),
            firstSchemas.Schemas
                .Select(value => (value.SchemaName, Kind: value.SchemaKind))
                .OrderBy(value => value.SchemaName, StringComparer.Ordinal)
                .ThenBy(value => value.Kind));
        Assert.Equal(
            firstHandlers.Handlers.Select(value => (value.HandlerName, value.CommandType, value.PlanFingerprint)),
            secondHandlers.Handlers.Select(value => (value.HandlerName, value.CommandType, value.PlanFingerprint)));
        Assert.Equal(
            firstSchemas.Schemas.Select(value => (value.SchemaName, value.SchemaKind, value.Fingerprint)),
            secondSchemas.Schemas.Select(value => (value.SchemaName, value.SchemaKind, value.Fingerprint)));
        Assert.Equal(expected: 0, (await client.GetHeadAsync(database)).EventId);

        ExecuteHandlerResponse[] results =
        [
            await ExecuteCommerceAsync(client, database,
                new PublishProduct(sku, "Native DCB Mug", 2_500, "USD"), 0xC001, 1),
            await ExecuteCommerceAsync(client, database,
                new ReceiveInventory($"{prefix}-receipt", sku, warehouse, 10), 0xC001, 2),
            await ExecuteCommerceAsync(client, database,
                new RegisterCustomer(customer, "Lifecycle Customer"), 0xC001, 3),
            await ExecuteCommerceAsync(client, database, new OpenCart(cart, customer, "USD"), 0xC001, 4),
            await ExecuteCommerceAsync(client, database,
                new ReserveCartLine(cart, line, customer, sku, warehouse, 2), 0xC001, 5),
            await ExecuteCommerceAsync(client, database, new CheckoutCart(cart, order, customer), 0xC001, 6),
            await ExecuteCommerceAsync(client, database,
                new DefineCoupon(coupon, 500, "USD", 10, 1), 0xC001, 7),
            await ExecuteCommerceAsync(client, database, new RedeemCoupon(coupon, customer, order), 0xC001, 8),
            await ExecuteCommerceAsync(client, database, new AuthorizePayment(payment, order), 0xC001, 9),
            await ExecuteCommerceAsync(client, database,
                new CreateShipment(shipment, order, customer, "Lifecycle Carrier"), 0xC001, 10),
            await ExecuteCommerceAsync(client, database,
                new ShipOrder(shipment, order, customer, "TRACK-LIFECYCLE"), 0xC001, 11),
            await ExecuteCommerceAsync(client, database, new DeliverOrder(shipment, order, customer), 0xC001, 12),
            await ExecuteCommerceAsync(client, database,
                new RequestReturn(returnId, order, line, customer, sku, 2, "Changed mind"), 0xC001, 13),
            await ExecuteCommerceAsync(client, database,
                new ReceiveReturn(returnId, order, line, customer, sku, warehouse, 2), 0xC001, 14),
            await ExecuteCommerceAsync(client, database,
                new RefundPayment($"{prefix}-refund", payment, order, returnId), 0xC001, 15)
        ];
        Assert.All(results, result =>
            Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, result.OutcomeCase));

        IReadOnlyList<SequencedEvent> events = await ReadCommerceEventsAsync(client, database);
        CommerceVerificationResult verification = CommerceCorrectness.VerifyHistory(events);
        Assert.True(verification.IsValid,
            string.Join(Environment.NewLine, verification.Issues.Select(value => value.Message)));
        Assert.Equal(Enumerable.Range(start: 1, count: 19).Select(value => (long)value),
            events.Select(value => value.EventId));
        Assert.Equal(
            [
                "ProductPublished",
                "InventoryReceived",
                "CustomerRegistered",
                "CartOpened",
                "CartLineReserved",
                "InventoryReserved",
                "CartCheckedOut",
                "OrderPlaced",
                "CouponDefined",
                "CouponRedeemed",
                "OrderDiscountApplied",
                "PaymentAuthorized",
                "ShipmentCreated",
                "OrderShipped",
                "OrderDelivered",
                "ReturnRequested",
                "ReturnReceived",
                "ReturnedInventoryRestocked",
                "PaymentRefunded"
            ],
            events.Select(value => value.Type));

        SequencedEvent reservedLine = events[4];
        Assert.Equal(2_500, reservedLine.Data.GetProperty("UnitPriceMinor").GetInt64());
        Assert.Equal(5_000, reservedLine.Data.GetProperty("LineTotalMinor").GetInt64());
        AssertCommerceKeys(reservedLine,
            ("cart", cart), ("line", line), ("customer", customer), ("sku", sku), ("warehouse", warehouse));
        SequencedEvent orderPlaced = events[7];
        Assert.Equal(1, orderPlaced.Data.GetProperty("LineCount").GetInt64());
        Assert.Equal(5_000, orderPlaced.Data.GetProperty("GrossTotalMinor").GetInt64());
        Assert.Equal("USD", orderPlaced.Data.GetProperty("Currency").GetString());
        SequencedEvent authorized = events[11];
        Assert.Equal(4_500, authorized.Data.GetProperty("AmountMinor").GetInt64());
        Assert.Equal("authorization-" + payment, authorized.Data.GetProperty("GatewayReference").GetString());
        AssertCommerceKeys(authorized, ("payment", payment), ("order", order));
        SequencedEvent restocked = events[17];
        Assert.Equal(2, restocked.Data.GetProperty("Quantity").GetInt64());
        AssertCommerceKeys(restocked, ("return", returnId), ("sku", sku), ("warehouse", warehouse));
        SequencedEvent refunded = events[18];
        Assert.Equal(4_500, refunded.Data.GetProperty("AmountMinor").GetInt64());
        Assert.Equal("USD", refunded.Data.GetProperty("Currency").GetString());
        Assert.Equal(expected: 19, (await client.GetHeadAsync(database)).EventId);
    }

    [Fact]
    public async Task Commerce_two_carts_racing_last_inventory_unit_commit_exactly_one_reservation()
    {
        const string database = "commerce-last-unit-race";
        const string sku = "race-sku";
        const string warehouse = "race-warehouse";
        using NativeDcbClient client = CreateCommerceClient();
        await CommerceSeeder.SeedAsync(client, database, CancellationToken.None);

        await ExecuteCommerceAsync(client, database, new PublishProduct(sku, "Last Unit", 1_000, "USD"),
            0xC002, 1);
        await ExecuteCommerceAsync(client, database, new ReceiveInventory("race-receipt", sku, warehouse, 1),
            0xC002, 2);
        await ExecuteCommerceAsync(client, database, new RegisterCustomer("race-customer-a", "Customer A"),
            0xC002, 3);
        await ExecuteCommerceAsync(client, database, new OpenCart("race-cart-a", "race-customer-a", "USD"),
            0xC002, 4);
        await ExecuteCommerceAsync(client, database, new RegisterCustomer("race-customer-b", "Customer B"),
            0xC002, 5);
        await ExecuteCommerceAsync(client, database, new OpenCart("race-cart-b", "race-customer-b", "USD"),
            0xC002, 6);

        ExecuteHandlerResponse[] raced = await Task.WhenAll(
            ExecuteCommerceAsync(client, database,
                new ReserveCartLine("race-cart-a", "race-line-a", "race-customer-a", sku, warehouse, 1),
                0xC002, 7),
            ExecuteCommerceAsync(client, database,
                new ReserveCartLine("race-cart-b", "race-line-b", "race-customer-b", sku, warehouse, 1),
                0xC002, 8));

        Assert.Single(raced, value => value.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Committed);
        ExecuteHandlerResponse rejected = Assert.Single(
            raced, value => value.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Rejected);
        Assert.Equal("Insufficient inventory", rejected.Rejected.Message);
        IReadOnlyList<SequencedEvent> events = await ReadCommerceEventsAsync(client, database);
        SequencedEvent reservation = Assert.Single(events, value => value.Type == nameof(InventoryReserved));
        Assert.Single(events, value => value.Type == nameof(CartLineReserved));
        Assert.Equal(1, reservation.Data.GetProperty("Quantity").GetInt64());
        Assert.Contains(reservation.Keys, value => value == new EventKey("sku", sku));
        Assert.Contains(reservation.Keys, value => value == new EventKey("warehouse", warehouse));
        Assert.Equal(expected: 8, (await client.GetHeadAsync(database)).EventId);
    }

    [Fact]
    public async Task Commerce_typed_remote_payment_completion_commits_once_and_stales_older_capability()
    {
        const string database = "commerce-remote-payment";
        const string sku = "remote-sku";
        const string warehouse = "remote-warehouse";
        const string customer = "remote-customer";
        const string cart = "remote-cart";
        const string line = "remote-line";
        const string order = "remote-order";
        const string coupon = "REMOTE-500";
        const string payment = "remote-payment";
        const string stalePayment = "remote-payment-stale";
        using NativeDcbClient client = CreateCommerceClient();
        await CommerceSeeder.SeedAsync(client, database, CancellationToken.None);

        await ExecuteCommerceAsync(client, database, new PublishProduct(sku, "Remote Item", 3_000, "USD"),
            0xC003, 1);
        await ExecuteCommerceAsync(client, database, new ReceiveInventory("remote-receipt", sku, warehouse, 2),
            0xC003, 2);
        await ExecuteCommerceAsync(client, database, new RegisterCustomer(customer, "Remote Customer"),
            0xC003, 3);
        await ExecuteCommerceAsync(client, database, new OpenCart(cart, customer, "USD"), 0xC003, 4);
        await ExecuteCommerceAsync(client, database,
            new ReserveCartLine(cart, line, customer, sku, warehouse, 2), 0xC003, 5);
        await ExecuteCommerceAsync(client, database, new CheckoutCart(cart, order, customer), 0xC003, 6);
        await ExecuteCommerceAsync(client, database, new DefineCoupon(coupon, 500, "USD", 10, 1), 0xC003, 7);
        await ExecuteCommerceAsync(client, database, new RedeemCoupon(coupon, customer, order), 0xC003, 8);

        PreparedDecision<PaymentPreparationModel> prepared = await client.PrepareDecisionAsync<
            AuthorizePayment, PaymentPreparationModel>(
            database, nameof(AuthorizePayment), new AuthorizePayment(payment, order), CommerceCommandId(0xC003, 9));
        PreparedDecision<PaymentPreparationModel> staleCandidate = await client.PrepareDecisionAsync<
            AuthorizePayment, PaymentPreparationModel>(
            database,
            nameof(AuthorizePayment),
            new AuthorizePayment(stalePayment, order),
            CommerceCommandId(0xC003, 10));

        Assert.True(prepared.IsPrepared);
        Assert.True(staleCandidate.IsPrepared);
        Assert.Equal(6_000, prepared.Model.GrossTotalMinor);
        Assert.Equal(500, prepared.Model.DiscountTotalMinor);
        Assert.Equal(5_500, prepared.Model.PayableAmountMinor);
        Assert.Equal("USD", prepared.Model.Currency);
        Assert.True(prepared.Model.OrderExists);
        Assert.False(prepared.Model.IsCancelled);
        Assert.False(prepared.Model.IsAuthorized);
        Assert.False(prepared.Model.IsDeclined);
        Assert.False(prepared.Model.PaymentAttemptExists);
        Assert.Equal(prepared.Model, staleCandidate.Model);

        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            database,
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(nameof(PaymentAuthorized),
                    new PaymentAuthorized(payment, order, 5_500, "USD", "gateway-remote-payment"))
            ]);
        CompleteDecisionResponse stale = await client.CompleteDecisionAsync(
            database,
            staleCandidate.ModelSignature,
            [
                new ProposedDecisionEvent(nameof(PaymentAuthorized),
                    new PaymentAuthorized(stalePayment, order, 5_500, "USD", "gateway-stale-payment"))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Committed, completed.OutcomeCase);
        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Stale, stale.OutcomeCase);
        Assert.Equal(expected: 12, stale.Stale.CurrentHead);
        EventEnvelope committed = Assert.Single(completed.Committed.Events);
        Assert.Equal(nameof(PaymentAuthorized), committed.Type);
        Assert.Equal(["payment", "order"], committed.Keys.Select(value => value.Key));
        Assert.Equal([payment, order], committed.Keys.Select(value => value.Value));
        IReadOnlyList<SequencedEvent> events = await ReadCommerceEventsAsync(client, database);
        Assert.Single(events, value => value.Type == nameof(PaymentAuthorized));
        Assert.Equal(expected: 12, (await client.GetHeadAsync(database)).EventId);
    }

    [Fact]
    public async Task Commerce_catalog_events_and_command_reconciliation_survive_restart()
    {
        const string database = "commerce-restart";
        Guid commandId = CommerceCommandId(0xC004, 1);
        PublishProduct command = new("restart-sku", "Restart Item", 1_200, "USD");
        using (NativeDcbClient client = CreateCommerceClient())
        {
            await CommerceSeeder.SeedAsync(client, database, CancellationToken.None);
            ExecuteHandlerResponse committed = await client.ExecuteHandlerAsync(
                database, nameof(PublishProduct), command, commandId);
            Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, committed.OutcomeCase);
        }

        _channel.Dispose();
        string databaseRoot = _factory.DatabaseRoot;
        _factory.PreserveDatabaseRoot = true;
        await _factory.DisposeAsync();
        _factory = new ServerFactory(databaseRoot);
        _channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });

        using NativeDcbClient reopened = CreateCommerceClient();
        Assert.Equal(expected: 1, (await reopened.GetHeadAsync(database)).EventId);
        Assert.Equal(expected: 24, (await reopened.ListHandlersAsync(database)).Handlers.Count);
        Assert.Equal(expected: 47, (await reopened.ListSchemasAsync(database)).Schemas.Count);

        ExecuteHandlerResponse replayed = await reopened.ExecuteHandlerAsync(
            database, nameof(PublishProduct), command, commandId);
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.AlreadyCommitted, replayed.OutcomeCase);
        GetEventsByCommandIdResponse reconciled = await reopened.GetEventsByCommandIdAsync(database, commandId);
        EventEnvelope persisted = Assert.Single(reconciled.Events);
        Assert.Equal(nameof(ProductPublished), persisted.Type);
        Assert.Equal(["sku"], persisted.Keys.Select(value => value.Key));
        Assert.Equal([command.Sku], persisted.Keys.Select(value => value.Value));
    }

    private NativeDcbClient CreateCommerceClient()
    {
        return new NativeDcbClient(
            new DatabaseService.DatabaseServiceClient(_channel),
            new CatalogService.CatalogServiceClient(_channel),
            new CommandService.CommandServiceClient(_channel),
            new EventService.EventServiceClient(_channel),
            new StatementService.StatementServiceClient(_channel),
            new AdministrationService.AdministrationServiceClient(_channel));
    }

    private static void AssertCommerceSdkBoundary<TCommand>(
        DecisionPlan ndl,
        DecisionDefinition<TCommand> definition,
        string sdkRejectionMessage)
        where TCommand : notnull
    {
        DecisionPlan sdk = definition.Compile(ndl.Name);
        Assert.Equal(ndl.CommandSchema, sdk.CommandSchema);
        Assert.Equal(
            ndl.Includes.Select(CommerceBoundary),
            sdk.Includes.Select(CommerceBoundary));
        Assert.Equal(ndl.Emissions.Select(value => value.EventType),
            sdk.Emissions.Select(value => value.EventType));

        PlanRequirement sdkRejection = Assert.Single(sdk.Evaluation.OfType<PlanRequirement>());
        PlanLiteralExpression reason = Assert.IsType<PlanLiteralExpression>(sdkRejection.Reason);
        Assert.Equal(sdkRejectionMessage, reason.Value);
        Assert.DoesNotContain(
            sdkRejectionMessage,
            ndl.Evaluation.OfType<PlanRequirement>()
                .Select(value => Assert.IsType<PlanLiteralExpression>(value.Reason).Value));
    }

    private static string CommerceBoundary(PlanInclude include)
    {
        return $"{include.EventType}:{string.Join(',', include.KeyBindings.Select(value => value.PropertyName))}";
    }

    private static async Task<ExecuteHandlerResponse> ExecuteCommerceAsync<TCommand>(
        NativeDcbClient client,
        string database,
        TCommand command,
        int commandScope,
        int commandOrdinal)
        where TCommand : notnull
    {
        return await client.ExecuteHandlerAsync(
            database,
            typeof(TCommand).Name,
            command,
            CommerceCommandId(commandScope, commandOrdinal));
    }

    private static Guid CommerceCommandId(int scope, int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinal);
        return CommerceCommandIds.Create((uint)scope, ordinal);
    }

    private static async Task<IReadOnlyList<SequencedEvent>> ReadCommerceEventsAsync(
        NativeDcbClient client,
        string database)
    {
        List<SequencedEvent> events = [];
        await foreach (SequencedEvent value in client.ReadEventsByRangeAsync(database))
        {
            events.Add(value);
        }

        return events;
    }

    private static void AssertCommerceKeys(SequencedEvent value, params (string Name, string Value)[] expected)
    {
        Assert.Equal(expected, value.Keys.Select(key => (key.Name, key.Value)));
    }
}