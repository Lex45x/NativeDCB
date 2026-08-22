using System.Collections.Immutable;
using System.Globalization;

namespace NativeDCB.Commerce.Fixtures;

public sealed class CommerceFixture
{
    private const ulong ProductSalt = 0xA0761D6478BD642FUL;
    private const ulong WarehouseSalt = 0xE7037ED1A0B428DBUL;
    private const ulong CustomerSalt = 0x8EBC6AF09C88C6E3UL;
    private const ulong CartSalt = 0x589965CC75374CC3UL;

    private readonly ImmutableArray<double> _productZipfianCdf;
    private readonly ImmutableArray<double> _warehouseZipfianCdf;
    private readonly ImmutableArray<double> _customerZipfianCdf;
    private readonly ImmutableArray<double> _cartZipfianCdf;

    public CommerceFixture(CommerceFixtureOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Options = options;
        Skus = CreateIds("sku", options.ProductCount);
        WarehouseIds = CreateIds("warehouse", options.WarehouseCount);
        CustomerIds = CreateIds("customer", options.CustomerCount);
        CartIds = CreateIds("cart", options.OpenCartCount);
        _productZipfianCdf = CreateZipfianCdf(Skus.Length, options.ZipfianExponent);
        _warehouseZipfianCdf = CreateZipfianCdf(WarehouseIds.Length, options.ZipfianExponent);
        _customerZipfianCdf = CreateZipfianCdf(CustomerIds.Length, options.ZipfianExponent);
        _cartZipfianCdf = CreateZipfianCdf(CartIds.Length, options.ZipfianExponent);
    }

    public CommerceFixtureOptions Options { get; }

    public ImmutableArray<string> Skus { get; }

    public ImmutableArray<string> WarehouseIds { get; }

    public ImmutableArray<string> CustomerIds { get; }

    public ImmutableArray<string> CartIds { get; }

    public string SelectSku(long sample, CommerceKeyDistribution distribution = CommerceKeyDistribution.Uniform)
    {
        return Select(Skus, _productZipfianCdf, sample, distribution, ProductSalt);
    }

    public string SelectWarehouseId(
        long sample,
        CommerceKeyDistribution distribution = CommerceKeyDistribution.Uniform)
    {
        return Select(WarehouseIds, _warehouseZipfianCdf, sample, distribution, WarehouseSalt);
    }

    public string SelectCustomerId(
        long sample,
        CommerceKeyDistribution distribution = CommerceKeyDistribution.Uniform)
    {
        return Select(CustomerIds, _customerZipfianCdf, sample, distribution, CustomerSalt);
    }

    public string SelectCartId(long sample, CommerceKeyDistribution distribution = CommerceKeyDistribution.Uniform)
    {
        return Select(CartIds, _cartZipfianCdf, sample, distribution, CartSalt);
    }

    public string ReceiptId(long ordinal) => CreateOrdinalId("receipt", ordinal);

    public string AdjustmentId(long ordinal) => CreateOrdinalId("adjustment", ordinal);

    public string LineId(long ordinal) => CreateOrdinalId("line", ordinal);

    public string OrderId(long ordinal) => CreateOrdinalId("order", ordinal);

    public string PaymentId(long ordinal) => CreateOrdinalId("payment", ordinal);

    public string ShipmentId(long ordinal) => CreateOrdinalId("shipment", ordinal);

    public string ReturnId(long ordinal) => CreateOrdinalId("return", ordinal);

    public string RefundId(long ordinal) => CreateOrdinalId("refund", ordinal);

    private static ImmutableArray<string> CreateIds(string prefix, int count)
    {
        ImmutableArray<string>.Builder builder = ImmutableArray.CreateBuilder<string>(count);
        for (long ordinal = 0; ordinal < count; ordinal++)
        {
            builder.Add(CreateOrdinalId(prefix, ordinal));
        }

        return builder.MoveToImmutable();
    }

    private static string CreateOrdinalId(string prefix, long ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{ordinal + 1:D8}");
    }

    private string Select(
        ImmutableArray<string> values,
        ImmutableArray<double> zipfianCdf,
        long sample,
        CommerceKeyDistribution distribution,
        ulong salt)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sample);
        if (values.IsEmpty)
        {
            throw new InvalidOperationException("The selected fixture key set is empty.");
        }

        ulong random = Mix(unchecked((ulong)Options.Seed) ^ unchecked((ulong)sample) ^ salt);
        int index = distribution switch
        {
            CommerceKeyDistribution.Uniform => (int)(random % (ulong)values.Length),
            CommerceKeyDistribution.Zipfian => SelectZipfian(zipfianCdf, ToUnitInterval(random)),
            CommerceKeyDistribution.HotKey => 0,
            _ => throw new ArgumentOutOfRangeException(
                nameof(distribution), distribution, "Unknown key distribution.")
        };
        return values[index];
    }

    private static ImmutableArray<double> CreateZipfianCdf(int count, double exponent)
    {
        if (count == 0)
        {
            return [];
        }

        double total = 0;
        for (int rank = 1; rank <= count; rank++)
        {
            total += 1 / Math.Pow(rank, exponent);
        }

        ImmutableArray<double>.Builder builder = ImmutableArray.CreateBuilder<double>(count);
        double cumulative = 0;
        for (int rank = 1; rank <= count; rank++)
        {
            cumulative += (1 / Math.Pow(rank, exponent)) / total;
            builder.Add(cumulative);
        }

        builder[^1] = 1;
        return builder.MoveToImmutable();
    }

    private static int SelectZipfian(ImmutableArray<double> cdf, double sample)
    {
        int low = 0;
        int high = cdf.Length - 1;
        while (low < high)
        {
            int middle = low + ((high - low) / 2);
            if (sample < cdf[middle])
            {
                high = middle;
            }
            else
            {
                low = middle + 1;
            }
        }

        return low;
    }

    private static double ToUnitInterval(ulong value)
    {
        return (value >> 11) * (1.0 / (1UL << 53));
    }

    private static ulong Mix(ulong value)
    {
        unchecked
        {
            value += 0x9E3779B97F4A7C15UL;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
