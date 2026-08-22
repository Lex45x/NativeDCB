using NativeDCB.Commerce.Events;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.Commerce.Workloads;

public static class CommerceQueries
{
    public static EventQuery Inventory(string sku, string warehouseId)
    {
        return ByKey(
            [
                nameof(InventoryReceived),
                nameof(InventoryAdjusted),
                nameof(InventoryReserved),
                nameof(InventoryReleased),
                nameof(ReturnedInventoryRestocked)
            ],
            new EventKey("sku", Required(sku, nameof(sku))),
            new EventKey("warehouse", Required(warehouseId, nameof(warehouseId))));
    }

    public static EventQuery Cart(string cartId)
    {
        return ByKey(
            [
                nameof(CartOpened),
                nameof(CartLineReserved),
                nameof(CartLineRemoved),
                nameof(InventoryReserved),
                nameof(InventoryReleased),
                nameof(CartCheckedOut),
                nameof(OrderPlaced)
            ],
            new EventKey("cart", Required(cartId, nameof(cartId))));
    }

    public static EventQuery Order(string orderId)
    {
        return ByKey(
            [
                nameof(CartCheckedOut),
                nameof(OrderPlaced),
                nameof(OrderCancelled),
                nameof(CouponRedeemed),
                nameof(OrderDiscountApplied),
                nameof(PaymentAuthorized),
                nameof(PaymentDeclined),
                nameof(PaymentRefunded),
                nameof(ShipmentCreated),
                nameof(OrderShipped),
                nameof(OrderDelivered),
                nameof(ReturnRequested),
                nameof(ReturnReceived)
            ],
            new EventKey("order", Required(orderId, nameof(orderId))));
    }

    public static EventQuery CustomerActivity(string customerId)
    {
        return ByKey(
            [
                nameof(CustomerRegistered),
                nameof(CartOpened),
                nameof(CartLineReserved),
                nameof(CartLineRemoved),
                nameof(CartCheckedOut),
                nameof(OrderPlaced),
                nameof(OrderCancelled),
                nameof(CouponRedeemed),
                nameof(OrderDiscountApplied),
                nameof(ShipmentCreated),
                nameof(OrderShipped),
                nameof(OrderDelivered),
                nameof(ReturnRequested),
                nameof(ReturnReceived)
            ],
            new EventKey("customer", Required(customerId, nameof(customerId))));
    }

    public static EventQuery Payment(string paymentId)
    {
        return ByKey(
            [nameof(PaymentAuthorized), nameof(PaymentDeclined), nameof(PaymentRefunded)],
            new EventKey("payment", Required(paymentId, nameof(paymentId))));
    }

    public static EventQuery OrderLifecycleSubscription()
    {
        return new EventQuery(
        [
            new QueryItem(
                [
                    nameof(OrderPlaced),
                    nameof(PaymentAuthorized),
                    nameof(OrderShipped),
                    nameof(OrderDelivered),
                    nameof(PaymentRefunded)
                ],
                [])
        ]);
    }

    public static EventQuery FullAudit() => EventQuery.All;

    private static EventQuery ByKey(IReadOnlyList<string> eventTypes, params EventKey[] keys)
    {
        return new EventQuery([new QueryItem(eventTypes, keys)]);
    }

    private static string Required(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value;
    }
}
