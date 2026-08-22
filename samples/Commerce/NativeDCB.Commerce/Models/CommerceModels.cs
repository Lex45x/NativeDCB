namespace NativeDCB.Commerce.Models;

public sealed record ProductModel(
    bool Exists,
    bool IsDiscontinued,
    long UnitPriceMinor,
    string? Currency);

public sealed record InventoryModel(
    bool ProductExists,
    bool ProductIsDiscontinued,
    bool ReceiptExists,
    bool AdjustmentExists,
    long AvailableQuantity);

public sealed record CartModel(
    bool Exists,
    string? CustomerId,
    string? Currency,
    bool IsCheckedOut);

public sealed record CartLineModel(
    string LineId,
    string CustomerId,
    string Sku,
    string WarehouseId,
    long Quantity,
    long UnitPriceMinor,
    long LineTotalMinor,
    string Currency,
    bool IsRemoved);

public sealed record CheckoutModel(
    bool CartExists,
    string? CustomerId,
    string? Currency,
    bool IsCheckedOut,
    bool OrderExists,
    long ActiveLineCount,
    long GrossTotalMinor);

public sealed record CouponModel(
    bool Exists,
    long DiscountMinor,
    string? Currency,
    long MaximumGlobalUses,
    long MaximumUsesPerCustomer,
    long GlobalUses,
    long CustomerUses);

public sealed record PaymentPreparationModel(
    bool OrderExists,
    long GrossTotalMinor,
    long DiscountTotalMinor,
    string? Currency,
    bool IsCancelled,
    bool IsAuthorized,
    bool IsDeclined,
    bool PaymentAttemptExists)
{
    public long PayableAmountMinor => GrossTotalMinor - DiscountTotalMinor;
}

public sealed record ShipmentModel(
    bool OrderExists,
    bool IsPaid,
    bool IsCancelled,
    bool ShipmentExists,
    bool IsShipped,
    bool IsDelivered);

public sealed record ReturnModel(
    bool OrderDelivered,
    bool ReturnExists,
    bool ReturnReceived,
    long Quantity);

public sealed record RefundPreparationModel(
    bool ReturnExists,
    bool ReturnReceived,
    string? ReturnOrderId,
    bool PaymentAuthorized,
    string? PaymentOrderId,
    bool RefundExists,
    bool ReturnAlreadyRefunded,
    long AuthorizedAmountMinor,
    long RefundedAmountMinor,
    string? Currency)
{
    public long RefundableAmountMinor => AuthorizedAmountMinor - RefundedAmountMinor;
}

public sealed record ProductPublicationSdkModel
{
    public bool? ProductExists { get; init; }
}

public sealed record ReserveCartLineSdkModel
{
    public bool? CartExists { get; init; }

    public string? CartCustomerId { get; init; }

    public string? CartCurrency { get; init; }

    public bool? CartCheckedOut { get; init; }

    public bool? ProductExists { get; init; }

    public bool? ProductActive { get; init; }

    public long? UnitPriceMinor { get; init; }

    public string? ProductCurrency { get; init; }

    public long? AvailableQuantity { get; init; }

    public bool? LineUsed { get; init; }
}

public sealed record ReserveCartLineSdkEvaluation(
    bool Accepted,
    long UnitPriceMinor,
    long LineTotalMinor,
    string Currency);

public sealed record CheckoutCartSdkModel
{
    public bool? CartExists { get; init; }

    public string? CartCustomerId { get; init; }

    public string? CartCurrency { get; init; }

    public bool? CartCheckedOut { get; init; }

    public bool? OrderExists { get; init; }

    public long? ActiveLineCount { get; init; }

    public long? GrossTotalMinor { get; init; }
}

public sealed record CheckoutCartSdkEvaluation(
    bool Accepted,
    long LineCount,
    long GrossTotalMinor,
    string Currency);
