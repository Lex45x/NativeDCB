using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;

using Grpc.Core;
using Grpc.Net.Client;

using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;

// Test-only contracts are consumed through JSON serialization.
// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable NotAccessedPositionalProperty.Local
namespace NativeDCB.EndToEndTests.Processes;

public sealed class CommerceSampleProcessTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(seconds: 90);
    private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(seconds: 30);

    [Fact]
    public async Task Commerce_sample_runs_against_a_real_server_process()
    {
        string databaseRoot = Path.Combine(
            Path.GetTempPath(),
            "NativeDCB.EndToEndTests",
            Guid.NewGuid().ToString("N"));
        string serverDirectory = Path.Combine(AppContext.BaseDirectory, "server");
        string serverAssembly = Path.Combine(serverDirectory, "NativeDCB.Server.dll");
        string sampleDirectory = Path.Combine(AppContext.BaseDirectory, "sample");
        string sampleAssembly = Path.Combine(sampleDirectory, "NativeDCB.Commerce.Sample.dll");
        Assert.True(File.Exists(serverAssembly), $"Server assembly was not copied to '{serverAssembly}'.");
        Assert.True(File.Exists(sampleAssembly), $"Sample assembly was not copied to '{sampleAssembly}'.");

        int[] ports = GetAvailablePorts(count: 3);
        string address = $"http://127.0.0.1:{ports[0]}";
        ConcurrentQueue<string> output = new();
        Process? server = null;
        using CancellationTokenSource timeout = new(TestTimeout);

        try
        {
            Directory.CreateDirectory(databaseRoot);
            server = StartServer(
                serverAssembly,
                serverDirectory,
                address,
                databaseRoot,
                ports[1],
                ports[2],
                output);

            using GrpcChannel channel = GrpcChannel.ForAddress(address);
            DatabaseService.DatabaseServiceClient databases = new(channel);
            CatalogService.CatalogServiceClient catalog = new(channel);
            CommandService.CommandServiceClient commands = new(channel);
            EventService.EventServiceClient events = new(channel);
            StatementService.StatementServiceClient statements = new(channel);
            AdministrationService.AdministrationServiceClient administration = new(channel);
            await WaitUntilReadyAsync(databases, server, output, timeout.Token);

            const string database = "commerce-process";
            await RunSeederAsync(sampleAssembly, sampleDirectory, address, database, timeout.Token);
            await RunSeederAsync(sampleAssembly, sampleDirectory, address, database, timeout.Token);

            using NativeDcbClient client = new(
                databases,
                catalog,
                commands,
                events,
                statements,
                administration);
            GetDatabaseInfoResponse info = await client.GetDatabaseInfoAsync(database, timeout.Token);
            Assert.Equal(expected: 0, info.Database.MainHead);
            Assert.Equal(
                [
                    ("command-schema", "AdjustInventory"),
                    ("command-schema", "AuthorizePayment"),
                    ("command-schema", "CancelOrder"),
                    ("command-schema", "ChangeProductPrice"),
                    ("command-schema", "CheckoutCart"),
                    ("command-schema", "CreateShipment"),
                    ("command-schema", "DefineCoupon"),
                    ("command-schema", "DeliverOrder"),
                    ("command-schema", "DiscontinueProduct"),
                    ("command-schema", "OpenCart"),
                    ("command-schema", "PublishProduct"),
                    ("command-schema", "ReceiveInventory"),
                    ("command-schema", "ReceiveReturn"),
                    ("command-schema", "RecordPaymentDeclined"),
                    ("command-schema", "RedeemCoupon"),
                    ("command-schema", "RefundPayment"),
                    ("command-schema", "RegisterCustomer"),
                    ("command-schema", "RemoveCartLine"),
                    ("command-schema", "RequestReturn"),
                    ("command-schema", "ReserveCartLine"),
                    ("command-schema", "ShipOrder"),
                    ("event-schema", "CartCheckedOut"),
                    ("event-schema", "CartLineRemoved"),
                    ("event-schema", "CartLineReserved"),
                    ("event-schema", "CartOpened"),
                    ("event-schema", "CouponDefined"),
                    ("event-schema", "CouponRedeemed"),
                    ("event-schema", "CustomerRegistered"),
                    ("event-schema", "InventoryAdjusted"),
                    ("event-schema", "InventoryReceived"),
                    ("event-schema", "InventoryReleased"),
                    ("event-schema", "InventoryReserved"),
                    ("event-schema", "OrderCancelled"),
                    ("event-schema", "OrderDelivered"),
                    ("event-schema", "OrderDiscountApplied"),
                    ("event-schema", "OrderPlaced"),
                    ("event-schema", "OrderShipped"),
                    ("event-schema", "PaymentAuthorized"),
                    ("event-schema", "PaymentDeclined"),
                    ("event-schema", "PaymentRefunded"),
                    ("event-schema", "ProductDiscontinued"),
                    ("event-schema", "ProductPriceChanged"),
                    ("event-schema", "ProductPublished"),
                    ("event-schema", "ReturnReceived"),
                    ("event-schema", "ReturnRequested"),
                    ("event-schema", "ReturnedInventoryRestocked"),
                    ("event-schema", "ShipmentCreated"),
                    ("handler", "AdjustInventory"),
                    ("handler", "AuthorizePayment"),
                    ("handler", "CancelOrder"),
                    ("handler", "ChangeProductPrice"),
                    ("handler", "CheckoutCart"),
                    ("handler", "CheckoutCartSdk"),
                    ("handler", "CreateShipment"),
                    ("handler", "DefineCoupon"),
                    ("handler", "DeliverOrder"),
                    ("handler", "DiscontinueProduct"),
                    ("handler", "OpenCart"),
                    ("handler", "PublishProduct"),
                    ("handler", "PublishProductSdk"),
                    ("handler", "ReceiveInventory"),
                    ("handler", "ReceiveReturn"),
                    ("handler", "RecordPaymentDeclined"),
                    ("handler", "RedeemCoupon"),
                    ("handler", "RefundPayment"),
                    ("handler", "RegisterCustomer"),
                    ("handler", "RemoveCartLine"),
                    ("handler", "RequestReturn"),
                    ("handler", "ReserveCartLine"),
                    ("handler", "ReserveCartLineSdk"),
                    ("handler", "ShipOrder")
                ],
                info.Database.CatalogFingerprints
                    .Select(item => (item.Kind, item.Name))
                    .OrderBy(item => item.Kind, StringComparer.Ordinal)
                    .ThenBy(item => item.Name, StringComparer.Ordinal));

            using AsyncServerStreamingCall<EventEnvelope> subscription = events.SubscribeEvents(
                new SubscribeEventsRequest { Database = database, AfterEventId = 0 },
                cancellationToken: timeout.Token);
            Task<IReadOnlyList<EventEnvelope>> publishedTask = ReadEventsAsync(
                subscription.ResponseStream, expectedCount: 8, timeout.Token);

            ExecuteHandlerResponse ndlPublication = await client.ExecuteHandlerAsync(
                database,
                "PublishProduct",
                new PublishProduct("process-ndl-sku", "NDL product", 2_500, "USD"),
                cancellationToken: timeout.Token);
            ExecuteHandlerResponse sdkPublication = await client.ExecuteHandlerAsync(
                database,
                "PublishProductSdk",
                new PublishProduct("process-sdk-sku", "SDK product", 3_000, "USD"),
                cancellationToken: timeout.Token);
            PreparedDecision<ProductPublicationModel> prepared = await client.PrepareDecisionAsync<
                PublishProduct, ProductPublicationModel>(
                database,
                "PublishProductSdk",
                new PublishProduct("process-remote-sku", "Remote product", 3_500, "USD"),
                cancellationToken: timeout.Token);

            Assert.True(prepared.IsPrepared);
            Assert.False(prepared.Model.ProductExists ?? false);
            CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
                database,
                prepared.ModelSignature,
                [
                    new ProposedDecisionEvent(
                        "ProductPublished",
                        new ProductPublished("process-remote-sku", "Remote product", 3_500, "USD"))
                ],
                timeout.Token);
            CompleteDecisionResponse replayed = await client.CompleteDecisionAsync(
                database,
                prepared.ModelSignature,
                [
                    new ProposedDecisionEvent(
                        "ProductPublished",
                        new ProductPublished("ignored", "Ignored replay", 1, "XXX"))
                ],
                timeout.Token);

            AssertCommitted(ndlPublication, eventId: 1, "ProductPublished");
            AssertCommitted(sdkPublication, eventId: 2, "ProductPublished");
            Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Committed, completed.OutcomeCase);
            Assert.Equal(expected: 3, completed.Committed.FirstEventId);
            Assert.Equal(expected: 3, completed.Committed.LastEventId);
            Assert.Equal("ProductPublished", Assert.Single(completed.Committed.Events).Type);
            Assert.Equal(prepared.CommandId.ToString("D"), completed.CommandId);
            Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.AlreadyCommitted, replayed.OutcomeCase);
            Assert.Equal(expected: 3, replayed.AlreadyCommitted.FirstEventId);
            Assert.Equal(expected: 3, replayed.AlreadyCommitted.LastEventId);
            Assert.Equal(completed.CommandId, replayed.CommandId);

            ExecuteHandlerResponse inventoryReceived = await client.ExecuteHandlerAsync(
                database,
                "ReceiveInventory",
                new ReceiveInventory("process-receipt", "process-remote-sku", "process-warehouse", 5),
                cancellationToken: timeout.Token);
            ExecuteHandlerResponse customerRegistered = await client.ExecuteHandlerAsync(
                database,
                "RegisterCustomer",
                new RegisterCustomer("process-customer", "Process Customer"),
                cancellationToken: timeout.Token);
            ExecuteHandlerResponse cartOpened = await client.ExecuteHandlerAsync(
                database,
                "OpenCart",
                new OpenCart("process-cart", "process-customer", "USD"),
                cancellationToken: timeout.Token);
            PreparedDecision<ReserveCartLineModel> reservationPrepared = await client.PrepareDecisionAsync<
                ReserveCartLine, ReserveCartLineModel>(
                database,
                "ReserveCartLineSdk",
                new ReserveCartLine(
                    "process-cart",
                    "process-line",
                    "process-customer",
                    "process-remote-sku",
                    "process-warehouse",
                    2),
                cancellationToken: timeout.Token);

            Assert.True(reservationPrepared.IsPrepared);
            Assert.True(reservationPrepared.Model.CartExists);
            Assert.Equal("process-customer", reservationPrepared.Model.CartCustomerId);
            Assert.Equal("USD", reservationPrepared.Model.CartCurrency);
            Assert.False(reservationPrepared.Model.CartCheckedOut ?? false);
            Assert.True(reservationPrepared.Model.ProductExists);
            Assert.True(reservationPrepared.Model.ProductActive);
            Assert.Equal(expected: 3_500, reservationPrepared.Model.UnitPriceMinor);
            Assert.Equal("USD", reservationPrepared.Model.ProductCurrency);
            Assert.Equal(expected: 5, reservationPrepared.Model.AvailableQuantity);
            Assert.False(reservationPrepared.Model.LineUsed ?? false);

            CompleteDecisionResponse reservationCompleted = await client.CompleteDecisionAsync(
                database,
                reservationPrepared.ModelSignature,
                [
                    new ProposedDecisionEvent(
                        "CartLineReserved",
                        new CartLineReserved(
                            "process-cart",
                            "process-line",
                            "process-customer",
                            "process-remote-sku",
                            "process-warehouse",
                            2,
                            3_500,
                            7_000,
                            "USD")),
                    new ProposedDecisionEvent(
                        "InventoryReserved",
                        new InventoryReserved(
                            "process-remote-sku",
                            "process-warehouse",
                            "process-cart",
                            "process-line",
                            2))
                ],
                timeout.Token);

            AssertCommitted(inventoryReceived, eventId: 4, "InventoryReceived");
            AssertCommitted(customerRegistered, eventId: 5, "CustomerRegistered");
            AssertCommitted(cartOpened, eventId: 6, "CartOpened");
            Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Committed, reservationCompleted.OutcomeCase);
            Assert.Equal(expected: 7, reservationCompleted.Committed.FirstEventId);
            Assert.Equal(expected: 8, reservationCompleted.Committed.LastEventId);
            Assert.Equal(
                ["CartLineReserved", "InventoryReserved"],
                reservationCompleted.Committed.Events.Select(value => value.Type));
            Assert.Equal(reservationPrepared.CommandId.ToString("D"), reservationCompleted.CommandId);

            IReadOnlyList<EventEnvelope> published = await publishedTask;
            IReadOnlyList<EventEnvelope> persisted = await ReadPersistedEventsAsync(
                events, database, throughEventId: 8, timeout.Token);
            Assert.Equal(expected: 8, published.Count);
            Assert.Equal(expected: 8, persisted.Count);
            Assert.Equal(Enumerable.Range(start: 1, count: 8).Select(value => (long)value),
                published.Select(@event => @event.EventId));
            Assert.Equal(
                [
                    "ProductPublished",
                    "ProductPublished",
                    "ProductPublished",
                    "InventoryReceived",
                    "CustomerRegistered",
                    "CartOpened",
                    "CartLineReserved",
                    "InventoryReserved"
                ],
                published.Select(@event => @event.Type));
            Assert.Equal(
                [
                    ndlPublication.CommandId,
                    sdkPublication.CommandId,
                    completed.CommandId,
                    inventoryReceived.CommandId,
                    customerRegistered.CommandId,
                    cartOpened.CommandId,
                    reservationCompleted.CommandId,
                    reservationCompleted.CommandId
                ],
                published.Select(@event => @event.CommandId));
            Assert.Equal(
                [
                    "PublishProduct",
                    "PublishProduct",
                    "PublishProduct",
                    "ReceiveInventory",
                    "RegisterCustomer",
                    "OpenCart",
                    "ReserveCartLine",
                    "ReserveCartLine"
                ],
                published.Select(@event => @event.CommandType));
            Assert.All(published, @event => Assert.Equal(expected: 1U, @event.SchemaVersion));

            AssertProductPayload(published[index: 0], "process-ndl-sku", "NDL product", 2_500, "USD");
            AssertProductPayload(published[index: 1], "process-sdk-sku", "SDK product", 3_000, "USD");
            AssertProductPayload(published[index: 2], "process-remote-sku", "Remote product", 3_500, "USD");
            AssertInventoryReceivedPayload(
                published[index: 3], "process-receipt", "process-remote-sku", "process-warehouse", 5);
            AssertCustomerRegisteredPayload(published[index: 4], "process-customer", "Process Customer");
            AssertCartOpenedPayload(published[index: 5], "process-cart", "process-customer", "USD");
            AssertCartLineReservedPayload(
                published[index: 6],
                "process-cart",
                "process-line",
                "process-customer",
                "process-remote-sku",
                "process-warehouse",
                2,
                3_500,
                7_000,
                "USD");
            AssertInventoryReservedPayload(
                published[index: 7],
                "process-remote-sku",
                "process-warehouse",
                "process-cart",
                "process-line",
                2);
            Assert.Equal([("sku", "process-ndl-sku")], Keys(published[index: 0]));
            Assert.Equal([("sku", "process-sdk-sku")], Keys(published[index: 1]));
            Assert.Equal([("sku", "process-remote-sku")], Keys(published[index: 2]));
            Assert.Equal(
                [
                    ("receipt", "process-receipt"),
                    ("sku", "process-remote-sku"),
                    ("warehouse", "process-warehouse")
                ],
                Keys(published[index: 3]));
            Assert.Equal([("customer", "process-customer")], Keys(published[index: 4]));
            Assert.Equal(
                [("cart", "process-cart"), ("customer", "process-customer")],
                Keys(published[index: 5]));
            Assert.Equal(
                [
                    ("cart", "process-cart"),
                    ("line", "process-line"),
                    ("customer", "process-customer"),
                    ("sku", "process-remote-sku"),
                    ("warehouse", "process-warehouse")
                ],
                Keys(published[index: 6]));
            Assert.Equal(
                [
                    ("sku", "process-remote-sku"),
                    ("warehouse", "process-warehouse"),
                    ("cart", "process-cart"),
                    ("line", "process-line")
                ],
                Keys(published[index: 7]));
            AssertEquivalent(published, persisted);
            Assert.Equal(expected: 8L, (await client.GetHeadAsync(database, timeout.Token)).EventId);
        }
        finally
        {
            await StopServerAsync(server);
            await DeleteDirectoryAsync(databaseRoot);
        }
    }

    private static Process StartServer(
        string serverAssembly,
        string workingDirectory,
        string address,
        string databaseRoot,
        int siloPort,
        int gatewayPort,
        ConcurrentQueue<string> output)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(serverAssembly);
        startInfo.Environment["ASPNETCORE_URLS"] = address;
        startInfo.Environment["DatabaseRoot"] = databaseRoot;
        startInfo.Environment["Orleans__SiloPort"] = siloPort.ToString();
        startInfo.Environment["Orleans__GatewayPort"] = gatewayPort.ToString();
        startInfo.Environment["RemoteDecisions__ActiveKeyId"] = "test";
        startInfo.Environment["RemoteDecisions__SigningKeys__test"] =
            "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

        Process process = new() { StartInfo = startInfo };
        process.OutputDataReceived += (_, args) => Capture(output, "stdout", args.Data);
        process.ErrorDataReceived += (_, args) => Capture(output, "stderr", args.Data);
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("NativeDCB.Server did not start.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return process;
    }

    private static async Task RunSeederAsync(
        string sampleAssembly,
        string workingDirectory,
        string address,
        string database,
        CancellationToken cancellationToken)
    {
        ProcessStartInfo startInfo = new("dotnet")
        {
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(sampleAssembly);
        startInfo.ArgumentList.Add("seed");
        startInfo.ArgumentList.Add(address);
        startInfo.ArgumentList.Add(database);

        using Process process = Process.Start(startInfo)
                                ?? throw new InvalidOperationException("Commerce sample seeder did not start.");
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(cancellationToken);
        Task<string> standardError = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        string output = await standardOutput;
        string error = await standardError;
        Assert.True(
            process.ExitCode == 0,
            $"Commerce sample seed exited with code {process.ExitCode}.{Environment.NewLine}{output}{Environment.NewLine}{error}");
        Assert.Contains($"Catalog ready in database '{database}'.", output, StringComparison.Ordinal);
    }

    private static async Task WaitUntilReadyAsync(
        DatabaseService.DatabaseServiceClient databases,
        Process server,
        ConcurrentQueue<string> output,
        CancellationToken cancellationToken)
    {
        using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        startup.CancelAfter(StartupTimeout);
        try
        {
            while (true)
            {
                startup.Token.ThrowIfCancellationRequested();
                if (server.HasExited)
                {
                    await server.WaitForExitAsync(startup.Token);
                    throw new InvalidOperationException(
                        $"NativeDCB.Server exited during startup with code {server.ExitCode}.{Environment.NewLine}{FormatOutput(output)}");
                }

                try
                {
                    GetHealthResponse health = await databases.GetHealthAsync(
                        new GetHealthRequest(),
                        deadline: DateTime.UtcNow.AddSeconds(value: 1),
                        cancellationToken: startup.Token);
                    if (health.Live)
                    {
                        return;
                    }
                }
                catch (RpcException exception) when (exception.StatusCode is
                                                         StatusCode.Unavailable or
                                                         StatusCode.DeadlineExceeded or
                                                         StatusCode.Cancelled)
                {
                    // Kestrel or Orleans can still be completing startup.
                }

                await Task.Delay(TimeSpan.FromMilliseconds(milliseconds: 100), startup.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"NativeDCB.Server was not healthy within {StartupTimeout}.{Environment.NewLine}{FormatOutput(output)}");
        }
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadPersistedEventsAsync(
        EventService.EventServiceClient events,
        string database,
        long throughEventId,
        CancellationToken cancellationToken)
    {
        ReadEventsByRangeRequest request = new()
        {
            Database = database,
            AfterEventId = 0,
            ThroughEventId = throughEventId,
            Mode = ReadMode.Snapshot
        };
        using AsyncServerStreamingCall<EventEnvelope> call = events.ReadEventsByRange(
            request, cancellationToken: cancellationToken);
        List<EventEnvelope> result = new();
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            result.Add(call.ResponseStream.Current);
        }

        return result;
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadEventsAsync(
        IAsyncStreamReader<EventEnvelope> stream,
        int expectedCount,
        CancellationToken cancellationToken)
    {
        List<EventEnvelope> result = [];
        while (result.Count < expectedCount && await stream.MoveNext(cancellationToken))
        {
            result.Add(stream.Current);
        }

        return result;
    }

    private static void AssertCommitted(ExecuteHandlerResponse response, long eventId, string eventType)
    {
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, response.OutcomeCase);
        Assert.True(Guid.TryParse(response.CommandId, out _));
        Assert.Equal(eventId, response.Committed.FirstEventId);
        Assert.Equal(eventId, response.Committed.LastEventId);
        Assert.Equal(eventType, Assert.Single(response.Committed.Events).Type);
    }

    private static void AssertProductPayload(
        EventEnvelope envelope,
        string sku,
        string name,
        long unitPriceMinor,
        string currency)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(sku, payload.RootElement.GetProperty("Sku").GetString());
        Assert.Equal(name, payload.RootElement.GetProperty("Name").GetString());
        Assert.Equal(unitPriceMinor, payload.RootElement.GetProperty("UnitPriceMinor").GetInt64());
        Assert.Equal(currency, payload.RootElement.GetProperty("Currency").GetString());
        Assert.Equal(expected: 4, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertInventoryReceivedPayload(
        EventEnvelope envelope,
        string receiptId,
        string sku,
        string warehouseId,
        long quantity)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(receiptId, payload.RootElement.GetProperty("ReceiptId").GetString());
        Assert.Equal(sku, payload.RootElement.GetProperty("Sku").GetString());
        Assert.Equal(warehouseId, payload.RootElement.GetProperty("WarehouseId").GetString());
        Assert.Equal(quantity, payload.RootElement.GetProperty("Quantity").GetInt64());
        Assert.Equal(expected: 4, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertCustomerRegisteredPayload(
        EventEnvelope envelope,
        string customerId,
        string displayName)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(customerId, payload.RootElement.GetProperty("CustomerId").GetString());
        Assert.Equal(displayName, payload.RootElement.GetProperty("DisplayName").GetString());
        Assert.Equal(expected: 2, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertCartOpenedPayload(
        EventEnvelope envelope,
        string cartId,
        string customerId,
        string currency)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(cartId, payload.RootElement.GetProperty("CartId").GetString());
        Assert.Equal(customerId, payload.RootElement.GetProperty("CustomerId").GetString());
        Assert.Equal(currency, payload.RootElement.GetProperty("Currency").GetString());
        Assert.Equal(expected: 3, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertCartLineReservedPayload(
        EventEnvelope envelope,
        string cartId,
        string lineId,
        string customerId,
        string sku,
        string warehouseId,
        long quantity,
        long unitPriceMinor,
        long lineTotalMinor,
        string currency)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(cartId, payload.RootElement.GetProperty("CartId").GetString());
        Assert.Equal(lineId, payload.RootElement.GetProperty("LineId").GetString());
        Assert.Equal(customerId, payload.RootElement.GetProperty("CustomerId").GetString());
        Assert.Equal(sku, payload.RootElement.GetProperty("Sku").GetString());
        Assert.Equal(warehouseId, payload.RootElement.GetProperty("WarehouseId").GetString());
        Assert.Equal(quantity, payload.RootElement.GetProperty("Quantity").GetInt64());
        Assert.Equal(unitPriceMinor, payload.RootElement.GetProperty("UnitPriceMinor").GetInt64());
        Assert.Equal(lineTotalMinor, payload.RootElement.GetProperty("LineTotalMinor").GetInt64());
        Assert.Equal(currency, payload.RootElement.GetProperty("Currency").GetString());
        Assert.Equal(expected: 9, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertInventoryReservedPayload(
        EventEnvelope envelope,
        string sku,
        string warehouseId,
        string cartId,
        string lineId,
        long quantity)
    {
        using JsonDocument payload = JsonDocument.Parse(envelope.DataJson.Memory);
        Assert.Equal(sku, payload.RootElement.GetProperty("Sku").GetString());
        Assert.Equal(warehouseId, payload.RootElement.GetProperty("WarehouseId").GetString());
        Assert.Equal(cartId, payload.RootElement.GetProperty("CartId").GetString());
        Assert.Equal(lineId, payload.RootElement.GetProperty("LineId").GetString());
        Assert.Equal(quantity, payload.RootElement.GetProperty("Quantity").GetInt64());
        Assert.Equal(expected: 5, payload.RootElement.EnumerateObject().Count());
    }

    private static void AssertEquivalent(
        IReadOnlyList<EventEnvelope> published,
        IReadOnlyList<EventEnvelope> persisted)
    {
        Assert.Equal(published.Select(value => value.EventId), persisted.Select(value => value.EventId));
        Assert.Equal(published.Select(value => value.Type), persisted.Select(value => value.Type));
        Assert.Equal(published.Select(value => value.SchemaVersion), persisted.Select(value => value.SchemaVersion));
        Assert.Equal(published.Select(value => value.CommandId), persisted.Select(value => value.CommandId));
        Assert.Equal(published.Select(value => value.CommandType), persisted.Select(value => value.CommandType));
        Assert.Equal(published.Select(value => value.TimestampUtc), persisted.Select(value => value.TimestampUtc));
        Assert.Equal(
            published.Select(value => Convert.ToBase64String(value.DataJson.Span)),
            persisted.Select(value => Convert.ToBase64String(value.DataJson.Span)));
        Assert.Equal(published.Select(value => Keys(value).ToArray()), persisted.Select(value => Keys(value).ToArray()));
    }

    private static IEnumerable<(string Key, string Value)> Keys(EventEnvelope envelope)
    {
        return envelope.Keys.Select(key => (key.Key, key.Value));
    }

    private static int[] GetAvailablePorts(int count)
    {
        HashSet<int> ports = [];
        while (ports.Count < count)
        {
            TcpListener listener = new(IPAddress.Loopback, port: 0);
            try
            {
                listener.Start();
                ports.Add(((IPEndPoint)listener.LocalEndpoint).Port);
            }
            finally
            {
                listener.Stop();
            }
        }

        return ports.ToArray();
    }

    private static async Task StopServerAsync(Process? process)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            using CancellationTokenSource stopTimeout = new(TimeSpan.FromSeconds(seconds: 10));
            await process.WaitForExitAsync(stopTimeout.Token);
        }
        catch (InvalidOperationException)
        {
            // The process exited between HasExited and Kill.
        }
        catch (OperationCanceledException)
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        finally
        {
            process.Dispose();
        }
    }

    private static async Task DeleteDirectoryAsync(string path)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception exception) when (
                attempt < 5 && exception is IOException or UnauthorizedAccessException)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(100 * attempt));
            }
        }

        Directory.Delete(path, recursive: true);
    }

    private static void Capture(ConcurrentQueue<string> output, string stream, string? line)
    {
        if (line is not null)
        {
            output.Enqueue($"[{stream}] {line}");
        }
    }

    private static string FormatOutput(ConcurrentQueue<string> output)
    {
        return output.IsEmpty ? "No server output was captured." : string.Join(Environment.NewLine, output);
    }

    private sealed record PublishProduct(
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        string Sku,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        string Name,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        long UnitPriceMinor,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Serialized as the command payload.
        string Currency);

    private sealed record ProductPublicationModel(bool? ProductExists);

    private sealed record ProductPublished(string Sku, string Name, long UnitPriceMinor, string Currency);

    private sealed record ReceiveInventory(string ReceiptId, string Sku, string WarehouseId, long Quantity);

    private sealed record RegisterCustomer(string CustomerId, string DisplayName);

    private sealed record OpenCart(string CartId, string CustomerId, string Currency);

    private sealed record ReserveCartLine(
        string CartId,
        string LineId,
        string CustomerId,
        string Sku,
        string WarehouseId,
        long Quantity);

    private sealed record ReserveCartLineModel(
        bool? CartExists,
        string? CartCustomerId,
        string? CartCurrency,
        bool? CartCheckedOut,
        bool? ProductExists,
        bool? ProductActive,
        long? UnitPriceMinor,
        string? ProductCurrency,
        long? AvailableQuantity,
        bool? LineUsed);

    private sealed record CartLineReserved(
        string CartId,
        string LineId,
        string CustomerId,
        string Sku,
        string WarehouseId,
        long Quantity,
        long UnitPriceMinor,
        long LineTotalMinor,
        string Currency);

    private sealed record InventoryReserved(
        string Sku,
        string WarehouseId,
        string CartId,
        string LineId,
        long Quantity);
}