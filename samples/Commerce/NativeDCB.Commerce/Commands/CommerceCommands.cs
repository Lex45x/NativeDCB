using NativeDCB.Sdk.Schemas;

namespace NativeDCB.Commerce.Commands;

[CommandType("PublishProduct")]
public sealed record PublishProduct(string Sku, string Name, long UnitPriceMinor, string Currency);

[CommandType("ChangeProductPrice")]
public sealed record ChangeProductPrice(string Sku, long UnitPriceMinor, string Currency);

[CommandType("DiscontinueProduct")]
public sealed record DiscontinueProduct(string Sku, string Reason);

[CommandType("ReceiveInventory")]
public sealed record ReceiveInventory(string ReceiptId, string Sku, string WarehouseId, long Quantity);

[CommandType("AdjustInventory")]
public sealed record AdjustInventory(
    string AdjustmentId,
    string Sku,
    string WarehouseId,
    long QuantityDelta,
    string Reason);

[CommandType("RegisterCustomer")]
public sealed record RegisterCustomer(string CustomerId, string DisplayName);

[CommandType("OpenCart")]
public sealed record OpenCart(string CartId, string CustomerId, string Currency);

[CommandType("ReserveCartLine")]
public sealed record ReserveCartLine(
    string CartId,
    string LineId,
    string CustomerId,
    string Sku,
    string WarehouseId,
    long Quantity);

[CommandType("RemoveCartLine")]
public sealed record RemoveCartLine(string CartId, string LineId);

[CommandType("CheckoutCart")]
public sealed record CheckoutCart(string CartId, string OrderId, string CustomerId);

[CommandType("DefineCoupon")]
public sealed record DefineCoupon(
    string CouponCode,
    long DiscountMinor,
    string Currency,
    long MaximumGlobalUses,
    long MaximumUsesPerCustomer);

[CommandType("RedeemCoupon")]
public sealed record RedeemCoupon(string CouponCode, string CustomerId, string OrderId);

[CommandType("AuthorizePayment")]
public sealed record AuthorizePayment(string PaymentId, string OrderId);

[CommandType("RecordPaymentDeclined")]
public sealed record RecordPaymentDeclined(string PaymentId, string OrderId, string Reason);

[CommandType("CancelOrder")]
public sealed record CancelOrder(string OrderId, string CustomerId, string Reason);

[CommandType("CreateShipment")]
public sealed record CreateShipment(string ShipmentId, string OrderId, string CustomerId, string Carrier);

[CommandType("ShipOrder")]
public sealed record ShipOrder(string ShipmentId, string OrderId, string CustomerId, string TrackingCode);

[CommandType("DeliverOrder")]
public sealed record DeliverOrder(string ShipmentId, string OrderId, string CustomerId);

[CommandType("RequestReturn")]
public sealed record RequestReturn(
    string ReturnId,
    string OrderId,
    string LineId,
    string CustomerId,
    string Sku,
    long Quantity,
    string Reason);

[CommandType("ReceiveReturn")]
public sealed record ReceiveReturn(
    string ReturnId,
    string OrderId,
    string LineId,
    string CustomerId,
    string Sku,
    string WarehouseId,
    long Quantity);

[CommandType("RefundPayment")]
public sealed record RefundPayment(string RefundId, string PaymentId, string OrderId, string ReturnId);
