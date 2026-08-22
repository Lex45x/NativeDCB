using NativeDCB.Commerce.Fixtures;
using NativeDCB.SystemBenchmarks.Scenarios;

namespace NativeDCB.SystemBenchmarks;

internal enum BenchmarkProfile { Smoke, Standard, Soak }
internal enum ScheduleMode { ClosedLoop, OpenLoop, SynchronizedBurst, CorrelatedWorkflow }
internal enum ProfilingMode { None, Counters, Trace, GcDump }

internal sealed record BenchmarkOptions
{
    public required BenchmarkScenario Strategy { get; init; }
    public string Scenario => Strategy.Slug;
    public BenchmarkProfile Profile { get; init; } = BenchmarkProfile.Smoke;
    public ScheduleMode Mode { get; init; }
    public IReadOnlyList<int> Concurrency { get; init; } = [1];
    public int? Rate { get; init; }
    public int? Operations { get; init; }
    public int Databases { get; init; } = 1;
    public long Seed { get; init; } = 104729;
    public CommerceFixtureProfile FixtureProfile { get; init; } = CommerceFixtureProfile.Tiny;
    public int? Products { get; init; }
    public int? Warehouses { get; init; }
    public int? Customers { get; init; }
    public int? Carts { get; init; }
    public long? PreloadedEvents { get; init; }
    public int PartitionLimit { get; init; } = 10_000;
    public TimeSpan Warmup { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan StartupTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan RpcTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public ProfilingMode Profiling { get; init; }
    public string ResultsRoot { get; init; } = Path.GetFullPath("BenchmarkResults");
    public string? StorageRoot { get; init; }
    public string? ServerAssembly { get; init; }
    public bool RetainStorage { get; init; }

    public CommerceFixtureOptions FixtureOptions => FixtureProfile == CommerceFixtureProfile.Custom
        ? CommerceFixtureOptions.Custom(Seed, Products!.Value, Warehouses!.Value, Customers!.Value,
            Carts!.Value, PreloadedEvents!.Value)
        : CommerceFixtureOptions.ForProfile(FixtureProfile, Seed);
}

internal sealed record ParseResult(BenchmarkOptions? Options, bool ShowHelp, bool ListScenarios);

internal static class CommandLine
{
    public static ParseResult Parse(string[] args)
    {
        Dictionary<string, string?> values = new(StringComparer.OrdinalIgnoreCase);
        bool help = false;
        bool list = false;
        for (int index = 0; index < args.Length; index++)
        {
            string token = args[index];
            if (token is "--help" or "-h" or "-?") { help = true; continue; }
            if (token == "--list-scenarios") { list = true; continue; }
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new ArgumentException($"Unexpected argument '{token}'. Options must use --name value.");

            int separator = token.IndexOf('=');
            string name = separator >= 0 ? token[..separator] : token;
            string? value = separator >= 0 ? token[(separator + 1)..] : null;
            if (name == "--retain-storage")
            {
                if (value is not null) throw new ArgumentException("--retain-storage does not accept a value.");
                values.Add(name, null);
                continue;
            }

            if (value is null)
            {
                if (++index >= args.Length || args[index].StartsWith("--", StringComparison.Ordinal))
                    throw new ArgumentException($"{name} requires a value.");
                value = args[index];
            }
            if (!KnownValueOptions.Contains(name)) throw new ArgumentException($"Unknown option '{name}'.");
            if (!values.TryAdd(name, value)) throw new ArgumentException($"Option '{name}' was specified more than once.");
        }

        if (help || list) return new ParseResult(null, help, list);
        string scenario = Required(values, "--scenario");
        BenchmarkScenario strategy = ScenarioCatalog.Get(scenario);
        BenchmarkProfile profile = ParseEnum(values, "--profile", BenchmarkProfile.Smoke,
            ("smoke", BenchmarkProfile.Smoke), ("standard", BenchmarkProfile.Standard), ("soak", BenchmarkProfile.Soak));
        (TimeSpan defaultWarmup, TimeSpan defaultDuration) = profile switch
        {
            BenchmarkProfile.Smoke => (TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5)),
            BenchmarkProfile.Standard => (TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(60)),
            BenchmarkProfile.Soak => (TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30)),
            _ => throw new ArgumentOutOfRangeException()
        };
        ScheduleMode mode = values.ContainsKey("--mode")
            ? ParseMode(Required(values, "--mode"))
            : strategy.DefaultMode;
        int databases = OptionalPositiveInt(values, "--databases") ?? 1;
        string defaultConcurrency = strategy.DefaultConcurrency(databases).ToString();
        IReadOnlyList<int> concurrency = ParsePositiveList(
            values.GetValueOrDefault("--concurrency") ?? defaultConcurrency, "--concurrency");
        int? rate = OptionalPositiveInt(values, "--rate");
        int? operations = OptionalPositiveInt(values, "--operations");
        int partitionLimit = OptionalPositiveInt(values, "--partition-limit") ??
                             strategy.DefaultPartitionLimit;
        if (mode == ScheduleMode.OpenLoop && rate is null)
            throw new ArgumentException("--mode open requires --rate; no machine-independent rate is assumed.");
        if (mode != ScheduleMode.OpenLoop && rate is not null)
            throw new ArgumentException("--rate is valid only with --mode open.");
        if (mode == ScheduleMode.OpenLoop && values.ContainsKey("--concurrency"))
            throw new ArgumentException("--concurrency is not accepted with --mode open; NBomber injection controls offered load.");
        strategy.ValidateSchedule(mode, concurrency, databases);
        if (mode == ScheduleMode.SynchronizedBurst && operations.HasValue &&
            concurrency.Any(value => operations.Value % value != 0))
            throw new ArgumentException("Burst --operations must be divisible by every requested concurrency value; partial cohorts never execute.");
        strategy.ValidateOperations(operations, databases, partitionLimit);

        CommerceFixtureProfile fixture = ParseEnum(values, "--fixture", CommerceFixtureProfile.Tiny,
            ("tiny", CommerceFixtureProfile.Tiny), ("small", CommerceFixtureProfile.Small),
            ("medium", CommerceFixtureProfile.Medium), ("custom", CommerceFixtureProfile.Custom));
        int? products = OptionalPositiveInt(values, "--products");
        int? warehouses = OptionalPositiveInt(values, "--warehouses");
        int? customers = OptionalPositiveInt(values, "--customers");
        int? carts = OptionalNonnegativeInt(values, "--carts");
        long? events = OptionalNonnegativeLong(values, "--preloaded-events");
        bool customValues = products.HasValue || warehouses.HasValue || customers.HasValue || carts.HasValue || events.HasValue;
        if (fixture == CommerceFixtureProfile.Custom && new object?[] { products, warehouses, customers, carts, events }.Any(x => x is null))
            throw new ArgumentException("--fixture custom requires --products, --warehouses, --customers, --carts, and --preloaded-events.");
        if (fixture != CommerceFixtureProfile.Custom && customValues)
            throw new ArgumentException("Custom fixture cardinalities require --fixture custom.");
        if (carts > customers) throw new ArgumentException("--carts cannot exceed --customers.");
        if (fixture == CommerceFixtureProfile.Custom &&
            events < checked((products!.Value * 2L) + customers!.Value + carts!.Value))
            throw new ArgumentException("Custom --preloaded-events must be at least 2*products + customers + carts so every selectable product is stocked and fixture entities exist.");

        BenchmarkOptions result = new()
        {
            Strategy = strategy,
            Profile = profile,
            Mode = mode,
            Concurrency = concurrency,
            Rate = rate,
            Operations = operations,
            Databases = databases,
            Seed = OptionalLong(values, "--seed") ?? 104729,
            FixtureProfile = fixture,
            Products = products,
            Warehouses = warehouses,
            Customers = customers,
            Carts = carts,
            PreloadedEvents = events,
            PartitionLimit = partitionLimit,
            Warmup = ParseDuration(values.GetValueOrDefault("--warmup"), defaultWarmup, "--warmup"),
            Duration = ParseDuration(values.GetValueOrDefault("--duration"), defaultDuration, "--duration"),
            StartupTimeout = ParseDuration(values.GetValueOrDefault("--startup-timeout"), TimeSpan.FromSeconds(60), "--startup-timeout"),
            RpcTimeout = ParseDuration(values.GetValueOrDefault("--rpc-timeout"), TimeSpan.FromSeconds(30), "--rpc-timeout"),
            Profiling = ParseEnum(values, "--profiling", ProfilingMode.None, ("none", ProfilingMode.None),
                ("counters", ProfilingMode.Counters), ("trace", ProfilingMode.Trace), ("gcdump", ProfilingMode.GcDump)),
            ResultsRoot = Path.GetFullPath(values.GetValueOrDefault("--results") ?? "BenchmarkResults"),
            StorageRoot = FullPathOrNull(values.GetValueOrDefault("--storage-root")),
            ServerAssembly = FullPathOrNull(values.GetValueOrDefault("--server")),
            RetainStorage = values.ContainsKey("--retain-storage")
        };
        if (mode == ScheduleMode.OpenLoop)
            OpenLoopPlan.OperationBudget(rate!.Value, result.Duration, operations);
        strategy.Validate(result);
        return new ParseResult(result, false, false);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("""
NativeDCB real-server system benchmarks

Usage:
  dotnet run -c Release --project benchmarks/NativeDCB.SystemBenchmarks -- --scenario <slug> [options]
  ... --list-scenarios

Core options:
  --scenario <slug>             One of the 12 slugs shown by --list-scenarios (required).
  --profile smoke|standard|soak Warmup/measurement: 2s/5s, 15s/60s, or 5m/30m. Default: smoke.
  --mode closed|open|burst|workflow
                                Scheduling model. Scenario-specific default is listed below.
                                Long aliases closed-loop, open-loop, synchronized-burst, and
                                correlated-workflow are accepted.
  --concurrency <n[,n...]>      Closed/burst/workflow workers. A list is an isolated run sweep.
  --rate <n>                    Open-loop arrivals per second; required for --mode open.
  --operations <n>              Exact measured operation budget. Defaults are scenario/profile-specific;
                                open defaults to ceil(rate*duration).
  --databases <n>               Fresh databases sharing one server. Default: 1.
  --seed <long>                 Deterministic fixture/workload seed. Default: 104729.
  --fixture tiny|small|medium|custom
  --products/--warehouses/--customers/--carts/--preloaded-events <n>
                                All required with custom; rejected with named profiles.
  --partition-limit <n>         Server partition event limit. Rollover default: 16; otherwise 10000.
  --warmup <duration>           Override profile warmup (examples: 500ms, 2s, 3m).
  --duration <duration>         Override measurement duration.
  --profiling none|counters|trace|gcdump
                                Attach installed dotnet tool to the server PID. Default: none.
  --server <dll>                Already-built NativeDCB.Server.dll; otherwise resolved from repo output.
  --results <directory>         Result bundle parent. Default: ./BenchmarkResults.
  --storage-root <directory>    Parent for fresh server roots; default is inside each result bundle.
  --retain-storage              Keep the fresh server database root after the run.
  --startup-timeout <duration>  Default: 60s. --rpc-timeout defaults to 30s.
  --help                        Print help. --list-scenarios prints the catalog and exits.

Rules:
  Open mode rejects --concurrency, requires one explicit --rate, plans evenly spaced arrivals,
  uses bounded workers, and drops arrivals delayed by at least one second. Each concurrency sweep value
  creates a fresh process/root/result bundle. Recovery is single-worker and rejects profiling because
  its server PID changes. Duplicate-command is burst-only and defaults to two workers per database.
  Independent-write and contention scenarios use prepared, bounded, iteration-terminated cohorts;
  --operations overrides defaults and --duration controls time-based defaults. Setup, warmup, verification,
  profiling shutdown, and cleanup are outside the measured interval. Remote model signatures are
  held only in memory and are never written to logs or result files.
""");
        ScenarioCatalog.Print();
    }

    private static readonly HashSet<string> KnownValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--scenario", "--profile", "--mode", "--concurrency", "--rate", "--operations", "--databases", "--seed",
        "--fixture", "--products", "--warehouses", "--customers", "--carts", "--preloaded-events",
        "--partition-limit", "--warmup", "--duration", "--profiling", "--server", "--results",
        "--storage-root", "--startup-timeout", "--rpc-timeout"
    };

    private static string Required(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out string? value) && !string.IsNullOrWhiteSpace(value)
            ? value : throw new ArgumentException($"{name} is required.");
    private static int? OptionalPositiveInt(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out string? value) ? ParseInt(value!, name, false) : null;
    private static int? OptionalNonnegativeInt(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out string? value) ? ParseInt(value!, name, true) : null;
    private static long? OptionalLong(Dictionary<string, string?> values, string name) =>
        values.TryGetValue(name, out string? value) && long.TryParse(value, out long result) ? result :
            values.ContainsKey(name) ? throw new ArgumentException($"{name} must be an integer.") : null;
    private static long? OptionalNonnegativeLong(Dictionary<string, string?> values, string name)
    {
        long? value = OptionalLong(values, name);
        if (value < 0) throw new ArgumentException($"{name} must be nonnegative.");
        return value;
    }
    private static int ParseInt(string value, string name, bool allowZero)
    {
        if (!int.TryParse(value, out int result) || result < (allowZero ? 0 : 1))
            throw new ArgumentException($"{name} must be {(allowZero ? "a nonnegative" : "a positive")} integer.");
        return result;
    }
    private static IReadOnlyList<int> ParsePositiveList(string value, string name)
    {
        int[] result = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(item => ParseInt(item, name, false)).Distinct().ToArray();
        return result.Length == 0 ? throw new ArgumentException($"{name} cannot be empty.") : result;
    }
    private static T ParseEnum<T>(Dictionary<string, string?> values, string name, T fallback, params (string, T)[] choices)
    {
        if (!values.TryGetValue(name, out string? text)) return fallback;
        foreach ((string key, T value) in choices) if (string.Equals(key, text, StringComparison.OrdinalIgnoreCase)) return value;
        throw new ArgumentException($"{name} must be one of: {string.Join(", ", choices.Select(x => x.Item1))}.");
    }
    private static ScheduleMode ParseMode(string value) => value.ToLowerInvariant() switch
    {
        "closed" or "closed-loop" => ScheduleMode.ClosedLoop,
        "open" or "open-loop" => ScheduleMode.OpenLoop,
        "burst" or "synchronized-burst" => ScheduleMode.SynchronizedBurst,
        "workflow" or "correlated-workflow" => ScheduleMode.CorrelatedWorkflow,
        _ => throw new ArgumentException("--mode must be closed, open, burst, or workflow.")
    };
    public static string ModeName(ScheduleMode mode) => mode switch
    {
        ScheduleMode.ClosedLoop => "closed",
        ScheduleMode.OpenLoop => "open",
        ScheduleMode.SynchronizedBurst => "burst",
        ScheduleMode.CorrelatedWorkflow => "workflow",
        _ => "unknown"
    };
    private static TimeSpan ParseDuration(string? text, TimeSpan fallback, string name)
    {
        if (text is null) return fallback;
        string suffix = new(text.SkipWhile(c => char.IsDigit(c) || c == '.').ToArray());
        string number = text[..^suffix.Length];
        if (!double.TryParse(number, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double value) || value <= 0)
            throw new ArgumentException($"{name} must be a positive duration such as 500ms, 5s, 2m, or 1h.");
        return suffix.ToLowerInvariant() switch
        {
            "ms" => TimeSpan.FromMilliseconds(value),
            "s" => TimeSpan.FromSeconds(value),
            "m" => TimeSpan.FromMinutes(value),
            "h" => TimeSpan.FromHours(value),
            _ => throw new ArgumentException($"{name} must end in ms, s, m, or h.")
        };
    }
    private static string? FullPathOrNull(string? value) => value is null ? null : Path.GetFullPath(value);
}

internal static class OpenLoopPlan
{
    public static int OperationBudget(int rate, TimeSpan duration, int? configuredOperations = null)
    {
        if (configuredOperations.HasValue) return configuredOperations.Value;
        double calculated = Math.Ceiling(rate * duration.TotalSeconds);
        if (!double.IsFinite(calculated) || calculated > int.MaxValue)
            throw new ArgumentException($"Open-loop rate and duration produce {calculated:G} operations, exceeding the supported maximum of {int.MaxValue}.");
        return Math.Max(1, (int)calculated);
    }

    public static int WorkerCount(int rate) => rate >= 128 ? 256 : Math.Max(4, rate * 2);
}