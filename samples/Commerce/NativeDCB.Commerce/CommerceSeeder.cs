using NativeDCB.Commerce.Commands;
using NativeDCB.Commerce.Events;
using NativeDCB.Generated;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Schemas;

namespace NativeDCB.Commerce;

public static class CommerceSeeder
{
    private static readonly HashSet<string> NdlHandlers = new(StringComparer.Ordinal)
    {
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
    };

    public static async Task SeedAsync(
        NativeDcbClient client,
        string database,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentException.ThrowIfNullOrWhiteSpace(database);
        RequireGeneratedSchemas(NativeDcbGeneratedSchemas.Create());

        ListDatabasesResponse databases = await client.ListDatabasesAsync(cancellationToken);
        if (!databases.Databases.Any(value => string.Equals(value.Database, database, StringComparison.Ordinal)))
        {
            CreateDatabaseResponse created = await client.CreateDatabaseAsync(database, cancellationToken);
            if (created.Database is null ||
                !string.Equals(created.Database.Database, database, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Database creation returned an invalid response for '{database}'.");
            }
        }

        RequireSchemaRegistration(nameof(ProductPublished),
            await client.RegisterEventSchemaAsync<ProductPublished>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ProductPriceChanged),
            await client.RegisterEventSchemaAsync<ProductPriceChanged>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ProductDiscontinued),
            await client.RegisterEventSchemaAsync<ProductDiscontinued>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(InventoryReceived),
            await client.RegisterEventSchemaAsync<InventoryReceived>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(InventoryAdjusted),
            await client.RegisterEventSchemaAsync<InventoryAdjusted>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(InventoryReserved),
            await client.RegisterEventSchemaAsync<InventoryReserved>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(InventoryReleased),
            await client.RegisterEventSchemaAsync<InventoryReleased>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ReturnedInventoryRestocked),
            await client.RegisterEventSchemaAsync<ReturnedInventoryRestocked>(database,
                cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CustomerRegistered),
            await client.RegisterEventSchemaAsync<CustomerRegistered>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CartOpened),
            await client.RegisterEventSchemaAsync<CartOpened>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CartLineReserved),
            await client.RegisterEventSchemaAsync<CartLineReserved>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CartLineRemoved),
            await client.RegisterEventSchemaAsync<CartLineRemoved>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CartCheckedOut),
            await client.RegisterEventSchemaAsync<CartCheckedOut>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(OrderPlaced),
            await client.RegisterEventSchemaAsync<OrderPlaced>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(OrderCancelled),
            await client.RegisterEventSchemaAsync<OrderCancelled>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CouponDefined),
            await client.RegisterEventSchemaAsync<CouponDefined>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CouponRedeemed),
            await client.RegisterEventSchemaAsync<CouponRedeemed>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(OrderDiscountApplied),
            await client.RegisterEventSchemaAsync<OrderDiscountApplied>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(PaymentAuthorized),
            await client.RegisterEventSchemaAsync<PaymentAuthorized>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(PaymentDeclined),
            await client.RegisterEventSchemaAsync<PaymentDeclined>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(PaymentRefunded),
            await client.RegisterEventSchemaAsync<PaymentRefunded>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ShipmentCreated),
            await client.RegisterEventSchemaAsync<ShipmentCreated>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(OrderShipped),
            await client.RegisterEventSchemaAsync<OrderShipped>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(OrderDelivered),
            await client.RegisterEventSchemaAsync<OrderDelivered>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ReturnRequested),
            await client.RegisterEventSchemaAsync<ReturnRequested>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ReturnReceived),
            await client.RegisterEventSchemaAsync<ReturnReceived>(database, cancellationToken: cancellationToken));

        RequireSchemaRegistration(nameof(PublishProduct),
            await client.RegisterCommandSchemaAsync<PublishProduct>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ChangeProductPrice),
            await client.RegisterCommandSchemaAsync<ChangeProductPrice>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(DiscontinueProduct),
            await client.RegisterCommandSchemaAsync<DiscontinueProduct>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ReceiveInventory),
            await client.RegisterCommandSchemaAsync<ReceiveInventory>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(AdjustInventory),
            await client.RegisterCommandSchemaAsync<AdjustInventory>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(RegisterCustomer),
            await client.RegisterCommandSchemaAsync<RegisterCustomer>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(OpenCart),
            await client.RegisterCommandSchemaAsync<OpenCart>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ReserveCartLine),
            await client.RegisterCommandSchemaAsync<ReserveCartLine>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(RemoveCartLine),
            await client.RegisterCommandSchemaAsync<RemoveCartLine>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CheckoutCart),
            await client.RegisterCommandSchemaAsync<CheckoutCart>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(DefineCoupon),
            await client.RegisterCommandSchemaAsync<DefineCoupon>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(RedeemCoupon),
            await client.RegisterCommandSchemaAsync<RedeemCoupon>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(AuthorizePayment),
            await client.RegisterCommandSchemaAsync<AuthorizePayment>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(RecordPaymentDeclined),
            await client.RegisterCommandSchemaAsync<RecordPaymentDeclined>(database,
                cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CancelOrder),
            await client.RegisterCommandSchemaAsync<CancelOrder>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(CreateShipment),
            await client.RegisterCommandSchemaAsync<CreateShipment>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ShipOrder),
            await client.RegisterCommandSchemaAsync<ShipOrder>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(DeliverOrder),
            await client.RegisterCommandSchemaAsync<DeliverOrder>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(RequestReturn),
            await client.RegisterCommandSchemaAsync<RequestReturn>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(ReceiveReturn),
            await client.RegisterCommandSchemaAsync<ReceiveReturn>(database, cancellationToken: cancellationToken));
        RequireSchemaRegistration(nameof(RefundPayment),
            await client.RegisterCommandSchemaAsync<RefundPayment>(database, cancellationToken: cancellationToken));

        string ndl = await CommerceNdl.LoadAsync(cancellationToken);
        await RegisterNdlHandlersAsync(client, database, ndl, cancellationToken);

        await RegisterSdkDecisionAsync(
            client,
            database,
            "PublishProductSdk",
            nameof(PublishProduct),
            CommerceDecisionDefinitions.PublishProductSdk(),
            cancellationToken);
        await RegisterSdkDecisionAsync(
            client,
            database,
            "ReserveCartLineSdk",
            nameof(ReserveCartLine),
            CommerceDecisionDefinitions.ReserveCartLineSdk(),
            cancellationToken);
        await RegisterSdkDecisionAsync(
            client,
            database,
            "CheckoutCartSdk",
            nameof(CheckoutCart),
            CommerceDecisionDefinitions.CheckoutCartSdk(),
            cancellationToken);
    }

    private static async Task RegisterNdlHandlersAsync(
        NativeDcbClient client,
        string database,
        string ndl,
        CancellationToken cancellationToken)
    {
        HashSet<string> registered = new(StringComparer.Ordinal);
        StatementCompletion? completion = null;

        await foreach (StatementResult result in client.ExecuteStatementAsync(
                           database,
                           ndl,
                           cancellationToken: cancellationToken))
        {
            if (completion is not null)
            {
                throw new InvalidOperationException("Commerce NDL returned results after statement completion.");
            }

            switch (result.ResultCase)
            {
                case StatementResult.ResultOneofCase.Registration:
                    RegistrationResult registration = result.Registration;
                    RequireNoErrors($"NDL handler '{registration.Name}'", registration.Diagnostics);
                    if (!string.Equals(registration.Kind, "handler", StringComparison.Ordinal) ||
                        !NdlHandlers.Contains(registration.Name))
                    {
                        throw new InvalidOperationException(
                            $"Commerce NDL returned unexpected registration '{registration.Kind}:{registration.Name}'.");
                    }

                    if (string.IsNullOrWhiteSpace(registration.SourceFingerprint) ||
                        string.IsNullOrWhiteSpace(registration.PlanFingerprint))
                    {
                        throw new InvalidOperationException(
                            $"NDL handler '{registration.Name}' was registered without valid fingerprints.");
                    }

                    if (!registered.Add(registration.Name))
                    {
                        throw new InvalidOperationException(
                            $"Commerce NDL registered handler '{registration.Name}' more than once.");
                    }

                    break;
                case StatementResult.ResultOneofCase.Diagnostics:
                    RequireNoErrors("Commerce NDL", result.Diagnostics.Diagnostics);
                    break;
                case StatementResult.ResultOneofCase.Completion:
                    completion = result.Completion;
                    if (!completion.Succeeded)
                    {
                        throw new InvalidOperationException(
                            $"Commerce NDL execution failed: {completion.Summary}");
                    }

                    break;
                default:
                    throw new InvalidOperationException(
                        $"Commerce NDL returned unexpected result '{result.ResultCase}' at statement " +
                        $"index {result.StatementIndex}.");
            }
        }

        if (completion is null)
        {
            throw new InvalidOperationException("Commerce NDL execution ended without a completion result.");
        }

        string[] missing = NdlHandlers.Except(registered, StringComparer.Ordinal).Order().ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidOperationException(
                $"Commerce NDL completed without registering handlers: {string.Join(", ", missing)}.");
        }
    }

    private static async Task RegisterSdkDecisionAsync<TCommand>(
        NativeDcbClient client,
        string database,
        string handlerName,
        string commandType,
        DecisionDefinition<TCommand> definition,
        CancellationToken cancellationToken)
        where TCommand : notnull
    {
        RegisterHandlerResponse response = await client.RegisterDecisionAsync(
            database,
            handlerName,
            definition,
            cancellationToken: cancellationToken);
        HandlerDescription handler = response.Handler;
        RequireNoErrors($"SDK handler '{handlerName}'", handler.Diagnostics);
        if (!handler.Valid ||
            !string.Equals(handler.HandlerName, handlerName, StringComparison.Ordinal) ||
            !string.Equals(handler.CommandType, commandType, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(handler.PlanFingerprint))
        {
            throw new InvalidOperationException(
                $"SDK handler '{handlerName}' was not published as a valid '{commandType}' handler.");
        }
    }

    private static void RequireSchemaRegistration(string schemaName, RegisterSchemaResponse response)
    {
        RequireNoErrors($"Schema '{schemaName}'", response.Diagnostics);
        if (string.IsNullOrWhiteSpace(response.Fingerprint))
        {
            throw new InvalidOperationException(
                $"Schema '{schemaName}' registration returned an empty fingerprint.");
        }
    }

    private static void RequireGeneratedSchemas(IReadOnlyList<SchemaDescriptor> schemas)
    {
        int eventCount = schemas.Count(value => value.SchemaType == SchemaType.Event);
        int commandCount = schemas.Count(value => value.SchemaType == SchemaType.Command);
        int distinctCount = schemas
            .Select(value => (value.SchemaType, value.Name))
            .Distinct()
            .Count();
        if (eventCount != 26 || commandCount != 21 || distinctCount != schemas.Count ||
            schemas.Any(value => value.ClrType.Assembly != typeof(CommerceSeeder).Assembly))
        {
            throw new InvalidOperationException(
                $"Commerce generated schemas are incomplete: {eventCount} events, {commandCount} commands, " +
                $"{distinctCount} distinct descriptors out of {schemas.Count}.");
        }
    }

    private static void RequireNoErrors(string operation, IEnumerable<Diagnostic> diagnostics)
    {
        string[] errors = diagnostics
            .Where(value => value.Severity == DiagnosticSeverity.Error)
            .Select(value => $"{value.Code}: {value.Message}")
            .ToArray();
        if (errors.Length > 0)
        {
            throw new InvalidOperationException($"{operation} failed: {string.Join("; ", errors)}");
        }
    }
}
