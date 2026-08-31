using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

using Grpc.Core;
using Grpc.Net.Client;

using NativeDCB.Protocol.V1;

namespace NativeDCB.SystemBenchmarks.Hosting;

internal sealed class ServerProcess : IAsyncDisposable
{
    private readonly BenchmarkOptions _options;
    private readonly string _logPath;
    private readonly SemaphoreSlim _lifecycle = new(1, 1);
    private Process? _process;
    private StreamWriter? _log;
    private volatile bool _stopping;
    private int _launchCount;

    public ServerProcess(BenchmarkOptions options, string databaseRoot, string logPath)
    {
        _options = options;
        DatabaseRoot = databaseRoot;
        _logPath = logPath;
        AllocateEndpoint();
        ServerAssembly = ResolveServerAssembly(options.ServerAssembly);
    }

    public string Address { get; private set; } = "";
    public string DatabaseRoot { get; }
    public string ServerAssembly { get; }
    public int KestrelPort { get; private set; }
    public int SiloPort { get; private set; }
    public int GatewayPort { get; private set; }
    public string LoggingLevel => _options.Strategy.ServerLoggingLevel;
    public Process Process => _process is { HasExited: false } process ? process :
        throw new InvalidOperationException("The server process is not running.");
    public Process? CurrentProcess => _process;
    public bool UnexpectedExit { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await LaunchAsync(cancellationToken);
        await WaitForLivenessAsync(cancellationToken);
    }

    public async Task LaunchAsync(CancellationToken cancellationToken)
    {
        await _lifecycle.WaitAsync(cancellationToken);
        try
        {
            if (_process is { HasExited: false }) throw new InvalidOperationException("Server is already running.");
            if (_launchCount++ > 0) AllocateEndpoint();
            Directory.CreateDirectory(DatabaseRoot);
            _log ??= new StreamWriter(new FileStream(_logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
            { AutoFlush = true };
            ProcessStartInfo start = new("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(ServerAssembly)!,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            start.ArgumentList.Add(ServerAssembly);
            start.Environment["ASPNETCORE_URLS"] = Address;
            start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
            start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
            start.Environment["Authentication__Providers"] = "Disabled";
            start.Environment["OTEL_SDK_DISABLED"] = "true";
            start.Environment["DatabaseRoot"] = DatabaseRoot;
            start.Environment["MaxEventCountPerPartition"] = _options.PartitionLimit.ToString();
            start.Environment["Orleans__SiloPort"] = SiloPort.ToString();
            start.Environment["Orleans__GatewayPort"] = GatewayPort.ToString();
            start.Environment["Logging__LogLevel__Default"] = LoggingLevel;
            start.Environment["RemoteDecisions__ActiveKeyId"] = "benchmark";
            // Benchmark-only ephemeral key. Never include environment values in logs or manifests.
            start.Environment["RemoteDecisions__SigningKeys__benchmark"] =
                "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

            Process process = new() { StartInfo = start, EnableRaisingEvents = true };
            process.Exited += (_, _) =>
            {
                if (!_stopping) UnexpectedExit = true;
            };
            process.OutputDataReceived += (_, e) => Capture("stdout", e.Data);
            process.ErrorDataReceived += (_, e) => Capture("stderr", e.Data);
            if (!process.Start()) throw new InvalidOperationException("NativeDCB.Server did not start.");
            _process = process;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            await _log.WriteLineAsync($"[{DateTimeOffset.UtcNow:O}] coordinator: server started pid={process.Id}");
        }
        finally { _lifecycle.Release(); }
    }

    public Task WaitForLivenessAsync(CancellationToken cancellationToken) => WaitUntilReadyAsync(cancellationToken);

    public async Task RestartAsync(CancellationToken cancellationToken)
    {
        await StopAsync();
        await StartAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            Process? process = _process;
            if (process is null) return;
            _stopping = true;
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(10));
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (InvalidOperationException) { }
            catch (OperationCanceledException)
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            finally
            {
                int? code = process.HasExited ? process.ExitCode : null;
                if (_log is not null)
                    await _log.WriteLineAsync($"[{DateTimeOffset.UtcNow:O}] coordinator: server stopped exit={code?.ToString() ?? "unknown"}");
                process.Dispose();
                _process = null;
                _stopping = false;
            }
        }
        finally { _lifecycle.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_log is not null) await _log.DisposeAsync();
        _lifecycle.Dispose();
    }

    private async Task WaitUntilReadyAsync(CancellationToken cancellationToken)
    {
        using CancellationTokenSource timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.StartupTimeout);
        using GrpcChannel channel = GrpcChannel.ForAddress(Address);
        DatabaseService.DatabaseServiceClient client = new(channel);
        try
        {
            while (true)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (_process is null || _process.HasExited)
                    throw new InvalidOperationException($"NativeDCB.Server exited during startup with code {_process?.ExitCode}.");
                try
                {
                    GetHealthResponse response = await client.GetHealthAsync(new GetHealthRequest(),
                        deadline: DateTime.UtcNow.AddSeconds(1), cancellationToken: timeout.Token);
                    if (response.Live) return;
                }
                catch (RpcException e) when (e.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded or StatusCode.Cancelled) { }
                await Task.Delay(100, timeout.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"NativeDCB.Server was not ready within {_options.StartupTimeout}.");
        }
    }

    private void Capture(string stream, string? line)
    {
        if (line is null || _log is null) return;
        lock (_log) _log.WriteLine($"[{DateTimeOffset.UtcNow:O}] [{stream}] {line}");
    }

    private static int[] AllocatePorts(int count)
    {
        HashSet<int> result = [];
        while (result.Count < count)
        {
            TcpListener listener = new(IPAddress.Loopback, 0);
            try { listener.Start(); result.Add(((IPEndPoint)listener.LocalEndpoint).Port); }
            finally { listener.Stop(); }
        }
        return result.ToArray();
    }

    private void AllocateEndpoint()
    {
        int[] ports = AllocatePorts(3);
        Address = $"http://127.0.0.1:{ports[0]}";
        KestrelPort = ports[0];
        SiloPort = ports[1];
        GatewayPort = ports[2];
    }

    private static string ResolveServerAssembly(string? explicitPath)
    {
        if (explicitPath is not null)
        {
            if (!File.Exists(explicitPath)) throw new FileNotFoundException("The --server assembly does not exist.", explicitPath);
            return explicitPath;
        }

        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory is not null && !Directory.Exists(Path.Combine(directory.FullName, "src", "NativeDCB.Server")))
            directory = directory.Parent;
        if (directory is null) throw new FileNotFoundException("Could not locate the repository root; use --server <NativeDCB.Server.dll>.");
        string configuration = AppContext.BaseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Debug" : "Release";
        string candidate = Path.Combine(directory.FullName, "src", "NativeDCB.Server", "bin", configuration, "net10.0", "NativeDCB.Server.dll");
        if (!File.Exists(candidate))
            throw new FileNotFoundException("The server build output was not found. Build this project first or pass --server.", candidate);
        return candidate;
    }
}