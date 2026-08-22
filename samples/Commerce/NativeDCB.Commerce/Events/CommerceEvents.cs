using NativeDCB.Sdk.Schemas;

namespace NativeDCB.Commerce.Events;

[EventType("ProductPublished")]
public sealed record ProductPublished(
    [property: ConsistencyKey("sku")] string Sku,
    string Name,
    long UnitPriceMinor,
    string Currency);

[EventType("ProductPriceChanged")]
public sealed record ProductPriceChanged(
    [property: ConsistencyKey("sku")] string Sku,
    long UnitPriceMinor,
    string Currency);

[EventType("ProductDiscontinued")]
public sealed record ProductDiscontinued(
    [property: ConsistencyKey("sku")] string Sku,
    string Reason);

[EventType("InventoryReceived")]
public sealed record InventoryReceived(
    [property: ConsistencyKey("receipt")] string ReceiptId,
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    long Quantity);

[EventType("InventoryAdjusted")]
public sealed record InventoryAdjusted(
    [property: ConsistencyKey("adjustment")] string AdjustmentId,
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    long QuantityDelta,
    string Reason);

[EventType("InventoryReserved")]
public sealed record InventoryReserved(
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("line")] string LineId,
    long Quantity);

[EventType("InventoryReleased")]
public sealed record InventoryReleased(
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("line")] string LineId,
    long Quantity,
    string Reason);

[EventType("ReturnedInventoryRestocked")]
public sealed record ReturnedInventoryRestocked(
    [property: ConsistencyKey("return")] string ReturnId,
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    long Quantity);

[EventType("CustomerRegistered")]
public sealed record CustomerRegistered(
    [property: ConsistencyKey("customer")] string CustomerId,
    string DisplayName);

[EventType("CartOpened")]
public sealed record CartOpened(
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("customer")] string CustomerId,
    string Currency);

[EventType("CartLineReserved")]
public sealed record CartLineReserved(
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("line")] string LineId,
    [property: ConsistencyKey("customer")] string CustomerId,
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    long Quantity,
    long UnitPriceMinor,
    long LineTotalMinor,
    string Currency);

[EventType("CartLineRemoved")]
public sealed record CartLineRemoved(
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("line")] string LineId,
    [property: ConsistencyKey("customer")] string CustomerId,
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    long Quantity,
    long LineTotalMinor,
    string Currency);

[EventType("CartCheckedOut")]
public sealed record CartCheckedOut(
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("customer")] string CustomerId,
    long LineCount,
    long GrossTotalMinor,
    string Currency);

[EventType("OrderPlaced")]
public sealed record OrderPlaced(
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("cart")] string CartId,
    [property: ConsistencyKey("customer")] string CustomerId,
    long LineCount,
    long GrossTotalMinor,
    string Currency);

[EventType("OrderCancelled")]
public sealed record OrderCancelled(
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("customer")] string CustomerId,
    string Reason);

[EventType("CouponDefined")]
public sealed record CouponDefined(
    [property: ConsistencyKey("coupon")] string CouponCode,
    long DiscountMinor,
    string Currency,
    long MaximumGlobalUses,
    long MaximumUsesPerCustomer);

[EventType("CouponRedeemed")]
public sealed record CouponRedeemed(
    [property: ConsistencyKey("coupon")] string CouponCode,
    [property: ConsistencyKey("customer")] string CustomerId,
    [property: ConsistencyKey("order")] string OrderId);

[EventType("OrderDiscountApplied")]
public sealed record OrderDiscountApplied(
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("coupon")] string CouponCode,
    [property: ConsistencyKey("customer")] string CustomerId,
    long DiscountMinor,
    string Currency);

[EventType("PaymentAuthorized")]
public sealed record PaymentAuthorized(
    [property: ConsistencyKey("payment")] string PaymentId,
    [property: ConsistencyKey("order")] string OrderId,
    long AmountMinor,
    string Currency,
    string GatewayReference);

[EventType("PaymentDeclined")]
public sealed record PaymentDeclined(
    [property: ConsistencyKey("payment")] string PaymentId,
    [property: ConsistencyKey("order")] string OrderId,
    string Reason);

[EventType("PaymentRefunded")]
public sealed record PaymentRefunded(
    [property: ConsistencyKey("refund")] string RefundId,
    [property: ConsistencyKey("payment")] string PaymentId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("return")] string ReturnId,
    long AmountMinor,
    string Currency,
    string GatewayReference);

[EventType("ShipmentCreated")]
public sealed record ShipmentCreated(
    [property: ConsistencyKey("shipment")] string ShipmentId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("customer")] string CustomerId,
    string Carrier);

[EventType("OrderShipped")]
public sealed record OrderShipped(
    [property: ConsistencyKey("shipment")] string ShipmentId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("customer")] string CustomerId,
    string TrackingCode);

[EventType("OrderDelivered")]
public sealed record OrderDelivered(
    [property: ConsistencyKey("shipment")] string ShipmentId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("customer")] string CustomerId);

[EventType("ReturnRequested")]
public sealed record ReturnRequested(
    [property: ConsistencyKey("return")] string ReturnId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("line")] string LineId,
    [property: ConsistencyKey("customer")] string CustomerId,
    [property: ConsistencyKey("sku")] string Sku,
    long Quantity,
    string Reason);

[EventType("ReturnReceived")]
public sealed record ReturnReceived(
    [property: ConsistencyKey("return")] string ReturnId,
    [property: ConsistencyKey("order")] string OrderId,
    [property: ConsistencyKey("line")] string LineId,
    [property: ConsistencyKey("customer")] string CustomerId,
    [property: ConsistencyKey("sku")] string Sku,
    [property: ConsistencyKey("warehouse")] string WarehouseId,
    long Quantity);
