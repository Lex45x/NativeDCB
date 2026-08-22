using System.Globalization;

using NativeDCB.Commerce.Fixtures;

namespace NativeDCB.Commerce.Workloads;

public static class CommerceCommandIds
{
    private const long MaximumOrdinal = 0xFFFFFFFFFFFF;

    public static Guid Create(uint scope, long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfZero(scope);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordinal, MaximumOrdinal);

        return Guid.ParseExact(
            $"{scope:X8}-0000-0000-0000-{ordinal.ToString("X12", CultureInfo.InvariantCulture)}",
            "D");
    }

    public static Guid ForFixture(long seed, long commandOrdinal)
    {
        return Create(ScopeForSeed(seed), commandOrdinal);
    }

    public static uint ScopeForSeed(long seed)
    {
        unchecked
        {
            ulong value = (ulong)seed + 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            uint scope = (uint)(value ^ (value >> 31));
            return scope == 0 ? 1u : scope;
        }
    }
}

public sealed record CommerceWorkloadSlot
{
    private const int CommandsPerSlot = 64;
    private const long MaximumSlotOrdinal = (0xFFFFFFFFFFFF - CommandsPerSlot) / CommandsPerSlot;

    private CommerceWorkloadSlot(
        long ordinal,
        uint commandScope,
        string sku,
        string warehouseId,
        string customerId,
        string cartId,
        string lineId,
        string orderId,
        string paymentId,
        string shipmentId,
        string returnId,
        string refundId,
        string receiptId)
    {
        Ordinal = ordinal;
        CommandScope = commandScope;
        Sku = sku;
        WarehouseId = warehouseId;
        CustomerId = customerId;
        CartId = cartId;
        LineId = lineId;
        OrderId = orderId;
        PaymentId = paymentId;
        ShipmentId = shipmentId;
        ReturnId = returnId;
        RefundId = refundId;
        ReceiptId = receiptId;
    }

    public long Ordinal { get; }

    public uint CommandScope { get; }

    public string Sku { get; }

    public string WarehouseId { get; }

    public string CustomerId { get; }

    public string CartId { get; }

    public string LineId { get; }

    public string OrderId { get; }

    public string PaymentId { get; }

    public string ShipmentId { get; }

    public string ReturnId { get; }

    public string RefundId { get; }

    public string ReceiptId { get; }

    public static CommerceWorkloadSlot Create(
        CommerceFixture fixture,
        uint commandScope,
        long ordinal,
        CommerceKeyDistribution distribution = CommerceKeyDistribution.Uniform)
    {
        ArgumentNullException.ThrowIfNull(fixture);
        ArgumentOutOfRangeException.ThrowIfZero(commandScope);
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(ordinal, MaximumSlotOrdinal);

        string suffix = (ordinal + 1).ToString("D12", CultureInfo.InvariantCulture);
        return new CommerceWorkloadSlot(
            ordinal,
            commandScope,
            fixture.SelectSku(ordinal, distribution),
            fixture.SelectWarehouseId(ordinal, distribution),
            $"workload-customer-{suffix}",
            $"workload-cart-{suffix}",
            $"workload-line-{suffix}",
            $"workload-order-{suffix}",
            $"workload-payment-{suffix}",
            $"workload-shipment-{suffix}",
            $"workload-return-{suffix}",
            $"workload-refund-{suffix}",
            $"workload-receipt-{suffix}");
    }

    public Guid CommandId(int operationOrdinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(operationOrdinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(operationOrdinal, CommandsPerSlot);
        long commandOrdinal = checked((Ordinal * CommandsPerSlot) + operationOrdinal);
        return CommerceCommandIds.Create(CommandScope, commandOrdinal);
    }
}
