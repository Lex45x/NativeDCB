using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using NativeDCB.SystemBenchmarks.Hosting;
using NativeDCB.SystemBenchmarks.Metrics;

namespace NativeDCB.SystemBenchmarks.Reporting;

internal sealed class ResultBundle
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private ResultBundle(string root, string storageRoot)
    {
        Root = root;
        StorageRoot = storageRoot;
        NBomberRoot = Path.Combine(root, "nbomber");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(NBomberRoot);
        using (File.Create(ServerLog)) { }
        using (StreamWriter metrics = File.CreateText(ProcessMetrics))
            metrics.WriteLine("utc,pid,cpu_total_ms,cpu_normalized_percent,working_set_bytes,private_bytes,threads,exited");
    }

    public string Root { get; }
    public string StorageRoot { get; }
    public string NBomberRoot { get; }
    public string ServerLog => Path.Combine(Root, "server.log");
    public string ProcessMetrics => Path.Combine(Root, "process-metrics.csv");

    public static ResultBundle Create(BenchmarkOptions options, int concurrency)
    {
        string id = Guid.NewGuid().ToString("N")[..8];
        string name = $"{DateTimeOffset.UtcNow:yyyyMMddTHHmmssZ}-{options.Scenario}-c{concurrency}-{id}";
        string root = Path.Combine(options.ResultsRoot, name);
        string storage = options.StorageRoot is null
            ? Path.Combine(root, "storage")
            : Path.Combine(options.StorageRoot, name);
        return new ResultBundle(root, storage);
    }

    public async Task WriteManifestAsync(BenchmarkOptions options, ServerProcess server, int concurrency,
        DateTimeOffset started, DateTimeOffset? ended, long? startingHead, long? fixtureBytes)
    {
        GitInfo git = await ReadGitAsync();
        DriveInfo? drive = TryDrive(StorageRoot);
        object manifest = new
        {
            schemaVersion = 1,
            run = new { startedUtc = started, endedUtc = ended, options.Scenario, profile = options.Profile.ToString().ToLowerInvariant(), mode = CommandLine.ModeName(options.Mode), concurrency, options.Rate, options.Operations, options.Databases, options.Seed },
            source = git,
            binaries = new
            {
                server = new { path = server.ServerAssembly, sha256 = Hash(server.ServerAssembly) },
                loadGenerator = new { path = Assembly.GetExecutingAssembly().Location, sha256 = Hash(Assembly.GetExecutingAssembly().Location) }
            },
            environment = new
            {
                runtime = RuntimeInformation.FrameworkDescription,
                os = RuntimeInformation.OSDescription,
                architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                cpu = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER"),
                processors = Environment.ProcessorCount,
                memoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes
            },
            storage = new { root = StorageRoot, drive = drive?.Name, format = drive?.DriveFormat, availableBytes = drive?.AvailableFreeSpace, fixtureBytes },
            fixture = new { profile = options.FixtureProfile.ToString().ToLowerInvariant(), options.FixtureOptions.ProductCount, options.FixtureOptions.WarehouseCount, options.FixtureOptions.CustomerCount, options.FixtureOptions.OpenCartCount, options.FixtureOptions.PreloadedEventCount, startingHead },
            server = new { address = server.Address, server.KestrelPort, server.SiloPort, server.GatewayPort, options.PartitionLimit, loggingLevel = server.LoggingLevel },
            profiling = new { mode = options.Profiling.ToString().ToLowerInvariant(), tool = ProfilerSession.ToolName(options.Profiling), version = await ProfilerSession.VersionAsync(options.Profiling) }
        };
        await WriteJsonAsync(Path.Combine(Root, "manifest.json"), manifest);
    }

    public Task WriteSummaryAsync(object value) => WriteJsonAsync(Path.Combine(Root, "summary.json"), value);

    public async Task WriteStorageSummaryAsync(string storageRoot)
    {
        string[] files = Directory.Exists(storageRoot) ? Directory.GetFiles(storageRoot, "*", SearchOption.AllDirectories) : [];
        object value = new
        {
            schemaVersion = 1,
            root = storageRoot,
            bytes = files.Sum(FileLength),
            files = files.Length,
            partitionFiles = files.Count(file => Path.GetFileName(file).StartsWith("store_partition_", StringComparison.OrdinalIgnoreCase)),
            stateFiles = files.Count(file => Path.GetFileName(file).StartsWith("state_", StringComparison.OrdinalIgnoreCase)),
            indexDirectories = CountIndexDirectories(storageRoot),
            immutableGenerations = files.Count(file => Path.GetFileName(file).StartsWith("generation_", StringComparison.OrdinalIgnoreCase) &&
                                                       Path.GetFileName(file).EndsWith("_v1.json", StringComparison.OrdinalIgnoreCase)),
            extensions = files.GroupBy(Path.GetExtension, StringComparer.OrdinalIgnoreCase).OrderBy(x => x.Key)
                .ToDictionary(x => string.IsNullOrEmpty(x.Key) ? "(none)" : x.Key, x => new { count = x.Count(), bytes = x.Sum(FileLength) })
        };
        await WriteJsonAsync(Path.Combine(Root, "storage-summary.json"), value);
    }

    public static async Task DeleteWithRetryAsync(string path)
    {
        for (int attempt = 1; attempt <= 5; attempt++)
        {
            try { if (Directory.Exists(path)) Directory.Delete(path, true); return; }
            catch (Exception e) when (attempt < 5 && e is IOException or UnauthorizedAccessException)
            { await Task.Delay(100 * attempt); }
        }
        if (Directory.Exists(path)) Directory.Delete(path, true);
    }

    public static long GetExistingFileBytes(string root)
    {
        try { return Directory.GetFiles(root, "*", SearchOption.AllDirectories).Sum(FileLength); }
        catch (DirectoryNotFoundException) { return 0; }
    }

    private static long FileLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (FileNotFoundException) { return 0; }
        catch (DirectoryNotFoundException) { return 0; }
    }

    private static int CountIndexDirectories(string root)
    {
        if (!Directory.Exists(root)) return 0;
        return Directory.GetDirectories(root, "indexes", SearchOption.AllDirectories)
            .Sum(indexRoot => Directory.GetDirectories(indexRoot, "index_*", SearchOption.TopDirectoryOnly).Length);
    }

    private static async Task WriteJsonAsync(string path, object value)
    {
        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, value, value.GetType(), Json);
    }
    private static string Hash(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }
    private static DriveInfo? TryDrive(string path)
    {
        try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))!); }
        catch { return null; }
    }
    private static async Task<GitInfo> ReadGitAsync()
    {
        string commit = await GitAsync("rev-parse", "HEAD") ?? "unknown";
        string branch = await GitAsync("branch", "--show-current") ?? "unknown";
        string? status = await GitAsync("status", "--porcelain");
        return new GitInfo(commit, branch, status is null ? null : status.Length > 0);
    }
    private static async Task<string?> GitAsync(params string[] args)
    {
        try
        {
            ProcessStartInfo start = new("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
            foreach (string arg in args) start.ArgumentList.Add(arg);
            using Process process = Process.Start(start)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output.Trim() : null;
        }
        catch { return null; }
    }
    private sealed record GitInfo(string Commit, string Branch, bool? Dirty);
}