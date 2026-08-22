using System.Collections.ObjectModel;
using System.Text.Json;

using NativeDCB.Commerce.Events;
using NativeDCB.Model.Events;

namespace NativeDCB.Commerce.Verification;

public sealed record CommerceVerificationIssue(string Code, string Message, long? EventId = null);

public sealed record CommerceHistorySummary(
    long StartingEventId,
    long EndingEventId,
    long EventCount,
    long CommandCount,
    long MinimumObservedInventory,
    IReadOnlyDictionary<string, long> EventTypeCounts);

public sealed record CommerceVerificationResult(
    CommerceHistorySummary Summary,
    IReadOnlyList<CommerceVerificationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public void ThrowIfInvalid()
    {
        if (!IsValid)
        {
            throw new InvalidOperationException(string.Join(
                Environment.NewLine,
                Issues.Select(issue => issue.EventId.HasValue
                    ? $"{issue.Code} at event {issue.EventId}: {issue.Message}"
                    : $"{issue.Code}: {issue.Message}")));
        }
    }
}

public static class CommerceCorrectness
{
    public static CommerceVerificationResult VerifyHistory(IEnumerable<SequencedEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        SequencedEvent[] history = events.ToArray();
        List<CommerceVerificationIssue> issues = [];
        Dictionary<string, long> typeCounts = new(StringComparer.Ordinal);
        Dictionary<(string Sku, string Warehouse), long> inventory = [];
        Dictionary<(string Cart, string Line), CartLineState> lines = [];
        Dictionary<string, CouponState> coupons = new(StringComparer.Ordinal);
        Dictionary<Guid, List<SequencedEvent>> commands = [];
        HashSet<Guid> completedCommands = [];
        HashSet<string> products = new(StringComparer.Ordinal);
        HashSet<string> checkedOutCarts = new(StringComparer.Ordinal);
        HashSet<string> orders = new(StringComparer.Ordinal);
        HashSet<string> authorizedPayments = new(StringComparer.Ordinal);
        HashSet<string> createdShipments = new(StringComparer.Ordinal);
        HashSet<string> shippedOrders = new(StringComparer.Ordinal);
        HashSet<string> deliveredOrders = new(StringComparer.Ordinal);
        HashSet<string> cancelledOrders = new(StringComparer.Ordinal);
        HashSet<string> returns = new(StringComparer.Ordinal);
        HashSet<string> refunds = new(StringComparer.Ordinal);
        Guid? currentCommand = null;
        long priorEventId = 0;
        long minimumInventory = 0;

        foreach (SequencedEvent @event in history)
        {
            typeCounts[@event.Type] = typeCounts.GetValueOrDefault(@event.Type) + 1;
            if (@event.EventId <= priorEventId)
            {
                Add("EventOrder", "Event IDs must be positive and strictly increasing.", @event);
            }

            priorEventId = Math.Max(priorEventId, @event.EventId);
            if (@event.CommandId == Guid.Empty)
            {
                Add("CommandId", "A persisted commerce event has an empty command ID.", @event);
            }
            else
            {
                if (currentCommand.HasValue && currentCommand.Value != @event.CommandId)
                {
                    completedCommands.Add(currentCommand.Value);
                }

                if (completedCommands.Contains(@event.CommandId))
                {
                    Add("CommandBatch", "Events for one command must form one contiguous batch.", @event);
                }

                currentCommand = @event.CommandId;
                if (!commands.TryGetValue(@event.CommandId, out List<SequencedEvent>? commandEvents))
                {
                    commandEvents = [];
                    commands.Add(@event.CommandId, commandEvents);
                }

                commandEvents.Add(@event);
            }

            try
            {
                switch (@event.Type)
                {
                    case nameof(ProductPublished):
                        AddUnique(products, String(@event, "Sku"), "ProductUniqueness", "SKU", @event, issues);
                        break;
                    case nameof(InventoryReceived):
                        ChangeInventory(@event, Long(@event, "Quantity"));
                        break;
                    case nameof(InventoryAdjusted):
                        ChangeInventory(@event, Long(@event, "QuantityDelta"));
                        break;
                    case nameof(InventoryReserved):
                        ChangeInventory(@event, -Long(@event, "Quantity"));
                        break;
                    case nameof(InventoryReleased):
                    case nameof(ReturnedInventoryRestocked):
                        ChangeInventory(@event, Long(@event, "Quantity"));
                        break;
                    case nameof(CartLineReserved):
                    {
                        (string Cart, string Line) key = (String(@event, "CartId"), String(@event, "LineId"));
                        if (!lines.TryAdd(key, new CartLineState(Long(@event, "LineTotalMinor"), IsRemoved: false)))
                        {
                            Add("LineUniqueness", $"Line '{key.Line}' was reused in cart '{key.Cart}'.", @event);
                        }

                        break;
                    }
                    case nameof(CartLineRemoved):
                    {
                        (string Cart, string Line) key = (String(@event, "CartId"), String(@event, "LineId"));
                        if (!lines.TryGetValue(key, out CartLineState? line) || line.IsRemoved)
                        {
                            Add("LineRemoval", $"Line '{key.Line}' was removed without one active reservation.", @event);
                        }
                        else
                        {
                            lines[key] = line with { IsRemoved = true };
                        }

                        break;
                    }
                    case nameof(CartCheckedOut):
                    {
                        string cart = String(@event, "CartId");
                        AddUnique(checkedOutCarts, cart, "CheckoutUniqueness", "cart", @event, issues);
                        CartLineState[] activeLines = lines
                            .Where(value => value.Key.Cart == cart && !value.Value.IsRemoved)
                            .Select(value => value.Value)
                            .ToArray();
                        if (activeLines.LongLength != Long(@event, "LineCount") ||
                            activeLines.Sum(value => value.LineTotalMinor) != Long(@event, "GrossTotalMinor"))
                        {
                            Add("CheckoutTotal", "Checkout line count or gross total does not match active cart lines.",
                                @event);
                        }

                        break;
                    }
                    case nameof(OrderPlaced):
                        AddUnique(orders, String(@event, "OrderId"), "OrderUniqueness", "order", @event, issues);
                        break;
                    case nameof(OrderCancelled):
                        AddUnique(cancelledOrders, String(@event, "OrderId"), "CancellationUniqueness", "order",
                            @event, issues);
                        break;
                    case nameof(CouponDefined):
                    {
                        string coupon = String(@event, "CouponCode");
                        if (!coupons.TryAdd(coupon, new CouponState(
                                Long(@event, "MaximumGlobalUses"),
                                Long(@event, "MaximumUsesPerCustomer"))))
                        {
                            Add("CouponUniqueness", $"Coupon '{coupon}' was defined more than once.", @event);
                        }

                        break;
                    }
                    case nameof(CouponRedeemed):
                        RedeemCoupon(@event);
                        break;
                    case nameof(PaymentAuthorized):
                        AddUnique(authorizedPayments, String(@event, "PaymentId"), "PaymentUniqueness", "payment",
                            @event, issues);
                        break;
                    case nameof(ShipmentCreated):
                        AddUnique(createdShipments, String(@event, "ShipmentId"), "ShipmentUniqueness", "shipment",
                            @event, issues);
                        break;
                    case nameof(OrderShipped):
                    {
                        string order = String(@event, "OrderId");
                        if (!createdShipments.Contains(String(@event, "ShipmentId")))
                        {
                            Add("ShipmentOrder", "An order was shipped before its shipment was created.", @event);
                        }

                        if (cancelledOrders.Contains(order))
                        {
                            Add("CancelledShipment", "A cancelled order was shipped.", @event);
                        }

                        shippedOrders.Add(order);
                        break;
                    }
                    case nameof(OrderDelivered):
                    {
                        string order = String(@event, "OrderId");
                        if (!shippedOrders.Contains(order))
                        {
                            Add("DeliveryOrder", "An order was delivered before it was shipped.", @event);
                        }

                        deliveredOrders.Add(order);
                        break;
                    }
                    case nameof(ReturnRequested):
                    {
                        string order = String(@event, "OrderId");
                        if (!deliveredOrders.Contains(order))
                        {
                            Add("ReturnOrder", "A return was requested before the order was delivered.", @event);
                        }

                        AddUnique(returns, String(@event, "ReturnId"), "ReturnUniqueness", "return", @event, issues);
                        break;
                    }
                    case nameof(PaymentRefunded):
                        AddUnique(refunds, String(@event, "RefundId"), "RefundUniqueness", "refund", @event, issues);
                        break;
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or FormatException or
                                              OverflowException)
            {
                Add("EventPayload", exception.Message, @event);
            }
        }

        foreach ((Guid _, List<SequencedEvent> batch) in commands)
        {
            SequencedEvent[] checkouts = batch.Where(value => value.Type == nameof(CartCheckedOut)).ToArray();
            SequencedEvent[] placements = batch.Where(value => value.Type == nameof(OrderPlaced)).ToArray();
            if (checkouts.Length > 1 || placements.Length > 1)
            {
                Add("CheckoutBatch", "A command batch contains duplicate checkout or order placement events.",
                    checkouts.FirstOrDefault() ?? placements[0]);
                continue;
            }

            SequencedEvent? checkedOut = checkouts.SingleOrDefault();
            SequencedEvent? placed = placements.SingleOrDefault();
            if ((checkedOut is null) != (placed is null))
            {
                SequencedEvent value = checkedOut ?? placed!;
                Add("CheckoutBatch", "Checkout and order placement must be emitted in the same command batch.", value);
            }
            else if (checkedOut is not null && placed is not null &&
                     (String(checkedOut, "OrderId") != String(placed, "OrderId") ||
                      Long(checkedOut, "LineCount") != Long(placed, "LineCount") ||
                      Long(checkedOut, "GrossTotalMinor") != Long(placed, "GrossTotalMinor")))
            {
                Add("CheckoutBatch", "Checkout and order placement batch summaries differ.", placed);
            }
        }

        CommerceHistorySummary summary = new(
            history.FirstOrDefault()?.EventId ?? 0,
            history.LastOrDefault()?.EventId ?? 0,
            history.LongLength,
            commands.Count,
            minimumInventory,
            new ReadOnlyDictionary<string, long>(typeCounts));
        return new CommerceVerificationResult(summary, issues.AsReadOnly());

        void ChangeInventory(SequencedEvent value, long delta)
        {
            (string Sku, string Warehouse) key = (String(value, "Sku"), String(value, "WarehouseId"));
            long available = checked(inventory.GetValueOrDefault(key) + delta);
            inventory[key] = available;
            minimumInventory = Math.Min(minimumInventory, available);
            if (available < 0)
            {
                Add("NegativeInventory",
                    $"Inventory for SKU '{key.Sku}' in warehouse '{key.Warehouse}' became {available}.", value);
            }
        }

        void RedeemCoupon(SequencedEvent value)
        {
            string code = String(value, "CouponCode");
            string customer = String(value, "CustomerId");
            if (!coupons.TryGetValue(code, out CouponState? coupon))
            {
                Add("CouponDefinition", $"Coupon '{code}' was redeemed before it was defined.", value);
                return;
            }

            coupon.GlobalUses++;
            coupon.CustomerUses[customer] = coupon.CustomerUses.GetValueOrDefault(customer) + 1;
            if (coupon.GlobalUses > coupon.MaximumGlobalUses ||
                coupon.CustomerUses[customer] > coupon.MaximumUsesPerCustomer)
            {
                Add("CouponLimit", $"Coupon '{code}' exceeded a configured usage limit.", value);
            }
        }

        void Add(string code, string message, SequencedEvent value)
        {
            issues.Add(new CommerceVerificationIssue(code, message, value.EventId));
        }
    }

    private static void AddUnique(
        HashSet<string> values,
        string value,
        string code,
        string description,
        SequencedEvent @event,
        ICollection<CommerceVerificationIssue> issues)
    {
        if (!values.Add(value))
        {
            issues.Add(new CommerceVerificationIssue(code, $"The {description} '{value}' was committed more than once.",
                @event.EventId));
        }
    }

    private static string String(SequencedEvent @event, string propertyName)
    {
        JsonElement value = Property(@event, propertyName);
        return value.GetString() ?? throw new InvalidOperationException(
            $"Event '{@event.Type}' property '{propertyName}' is null.");
    }

    private static long Long(SequencedEvent @event, string propertyName)
    {
        return Property(@event, propertyName).GetInt64();
    }

    private static JsonElement Property(SequencedEvent @event, string propertyName)
    {
        if (@event.Data.TryGetProperty(propertyName, out JsonElement value))
        {
            return value;
        }

        string camelCase = JsonNamingPolicy.CamelCase.ConvertName(propertyName);
        if (@event.Data.TryGetProperty(camelCase, out value))
        {
            return value;
        }

        throw new InvalidOperationException($"Event '{@event.Type}' has no '{propertyName}' property.");
    }

    private sealed record CartLineState(long LineTotalMinor, bool IsRemoved);

    private sealed class CouponState(long maximumGlobalUses, long maximumUsesPerCustomer)
    {
        public long MaximumGlobalUses { get; } = maximumGlobalUses;

        public long MaximumUsesPerCustomer { get; } = maximumUsesPerCustomer;

        public long GlobalUses { get; set; }

        public Dictionary<string, long> CustomerUses { get; } = new(StringComparer.Ordinal);
    }
}
