using NativeDCB.SystemBenchmarks.Scenarios;
using NativeDCB.SystemBenchmarks.Scenarios.Strategies;

namespace NativeDCB.SystemBenchmarks;

internal static class ScenarioCatalog
{
    public static readonly IReadOnlyList<BenchmarkScenario> All =
    [
        new IndependentWritesScenario(),
        new HotInventoryScenario(),
        new ReservationCheckoutScenario(),
        new DuplicateCommandScenario(),
        new CompletePurchaseScenario(),
        new RemotePaymentScenario(),
        new QueriesScenario(),
        new RangeStreamingScenario(),
        new SubscriptionsScenario(),
        new MixedScenario(),
        new PartitionRolloverScenario(),
        new RecoveryScenario()
    ];

    public static BenchmarkScenario Get(string slug) => All.FirstOrDefault(x => x.Slug == slug) ??
        throw new ArgumentException($"Unknown scenario '{slug}'. Use --list-scenarios.");

    public static void Print()
    {
        Console.WriteLine("Scenarios:");
        foreach (BenchmarkScenario scenario in All)
            Console.WriteLine($"  {scenario.Slug,-22} default={CommandLine.ModeName(scenario.DefaultMode),-8} {scenario.Description}");
    }
}