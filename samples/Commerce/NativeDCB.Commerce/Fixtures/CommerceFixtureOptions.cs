namespace NativeDCB.Commerce.Fixtures;

public enum CommerceFixtureProfile
{
    Tiny,
    Small,
    Medium,
    Custom
}

public enum CommerceKeyDistribution
{
    Uniform,
    Zipfian,
    HotKey
}

public sealed record CommerceFixtureOptions
{
    public CommerceFixtureOptions(
        CommerceFixtureProfile profile,
        long seed,
        int productCount,
        int warehouseCount,
        int customerCount,
        int openCartCount,
        long preloadedEventCount,
        double zipfianExponent = 1.1)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(productCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warehouseCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(customerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(openCartCount);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(openCartCount, customerCount);
        ArgumentOutOfRangeException.ThrowIfNegative(preloadedEventCount);
        if (!double.IsFinite(zipfianExponent) || zipfianExponent <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(zipfianExponent),
                zipfianExponent,
                "The Zipfian exponent must be finite and positive.");
        }

        Profile = profile;
        Seed = seed;
        ProductCount = productCount;
        WarehouseCount = warehouseCount;
        CustomerCount = customerCount;
        OpenCartCount = openCartCount;
        PreloadedEventCount = preloadedEventCount;
        ZipfianExponent = zipfianExponent;
    }

    public CommerceFixtureProfile Profile { get; }

    public long Seed { get; }

    public int ProductCount { get; }

    public int WarehouseCount { get; }

    public int CustomerCount { get; }

    public int OpenCartCount { get; }

    public long PreloadedEventCount { get; }

    public double ZipfianExponent { get; }

    public static CommerceFixtureOptions ForProfile(CommerceFixtureProfile profile, long seed = 1)
    {
        return profile switch
        {
            CommerceFixtureProfile.Tiny => new(profile, seed, 10, 2, 20, 10, 100),
            CommerceFixtureProfile.Small => new(profile, seed, 100, 4, 1_000, 250, 10_000),
            CommerceFixtureProfile.Medium => new(profile, seed, 1_000, 8, 10_000, 2_000, 100_000),
            CommerceFixtureProfile.Custom => throw new ArgumentException(
                "Custom profiles require explicit counts.", nameof(profile)),
            _ => throw new ArgumentOutOfRangeException(nameof(profile), profile, "Unknown fixture profile.")
        };
    }

    public static CommerceFixtureOptions Custom(
        long seed,
        int productCount,
        int warehouseCount,
        int customerCount,
        int openCartCount,
        long preloadedEventCount,
        double zipfianExponent = 1.1)
    {
        return new CommerceFixtureOptions(
            CommerceFixtureProfile.Custom,
            seed,
            productCount,
            warehouseCount,
            customerCount,
            openCartCount,
            preloadedEventCount,
            zipfianExponent);
    }
}
