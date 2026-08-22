using System.Diagnostics;
using System.Globalization;

namespace NativeDCB.SystemBenchmarks.Metrics;

internal sealed class ProfilerSession : IAsyncDisposable
{
    private readonly ProfilingMode _mode;
    private readonly int _pid;
    private readonly string _bundle;
    private readonly TimeSpan _collectionDuration;
    private Process? _process;
    private Process? _heapProcess;
    private bool _stopped;

    public ProfilerSession(ProfilingMode mode, int pid, string bundle, TimeSpan measurementDuration)
    {
        _mode = mode;
        _pid = pid;
        _bundle = bundle;
        _collectionDuration = measurementDuration + TimeSpan.FromSeconds(10);
    }

    public static string ToolName(ProfilingMode mode) => mode switch
    {
        ProfilingMode.Counters => "dotnet-counters",
        ProfilingMode.Trace => "dotnet-trace",
        ProfilingMode.GcDump => "dotnet-gcdump",
        _ => "none"
    };

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _stopped = false;
        if (_mode == ProfilingMode.None || _mode == ProfilingMode.GcDump) return;
        string tool = ToolName(_mode);
        await EnsureToolAsync(tool, cancellationToken);
        ProcessStartInfo start = QuietStart(tool);
        if (_mode == ProfilingMode.Counters)
        {
            Add(start, "collect", "--process-id", _pid.ToString(), "--refresh-interval", "1", "--format", "csv",
                "--duration", _collectionDuration.ToString("c", CultureInfo.InvariantCulture),
                "--output", Path.Combine(_bundle, "optional.counters.csv"));
        }
        else
        {
            Add(start, "collect", "--process-id", _pid.ToString(), "--duration",
                _collectionDuration.ToString("c", CultureInfo.InvariantCulture), "--output", Path.Combine(_bundle, "optional.nettrace"));
        }
        _process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        _process.OutputDataReceived += (_, _) => { };
        _process.ErrorDataReceived += (_, _) => { };
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        await Task.Delay(500, cancellationToken);
        if (_process.HasExited && _process.ExitCode != 0)
        {
            _process.Dispose();
            _process = null;
            throw new InvalidOperationException($"{tool} exited while attaching to server PID {_pid}.");
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_stopped) return;
        _stopped = true;
        if (_mode == ProfilingMode.GcDump)
        {
            const string tool = "dotnet-gcdump";
            await EnsureToolAsync(tool, cancellationToken);
            ProcessStartInfo start = QuietStart(tool);
            Add(start, "collect", "--process-id", _pid.ToString(), "--output", Path.Combine(_bundle, "optional.gcdump"));
            _heapProcess = Process.Start(start) ?? throw new InvalidOperationException("Could not start dotnet-gcdump.");
            _heapProcess.OutputDataReceived += (_, _) => { };
            _heapProcess.ErrorDataReceived += (_, _) => { };
            _heapProcess.BeginOutputReadLine();
            _heapProcess.BeginErrorReadLine();
            try
            {
                await _heapProcess.WaitForExitAsync(cancellationToken);
                if (_heapProcess.ExitCode != 0) throw new InvalidOperationException("dotnet-gcdump failed to collect the server heap.");
            }
            finally
            {
                if (!_heapProcess.HasExited) _heapProcess.Kill(entireProcessTree: true);
                _heapProcess.Dispose();
                _heapProcess = null;
            }
            return;
        }
        if (_process is null) return;
        try
        {
            if (!_process.HasExited)
            {
                using CancellationTokenSource finish = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                finish.CancelAfter(TimeSpan.FromSeconds(15));
                try { await _process.WaitForExitAsync(finish.Token); }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _process.Kill(entireProcessTree: true);
                    await _process.WaitForExitAsync(cancellationToken);
                }
            }
        }
        finally
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                await _process.WaitForExitAsync(CancellationToken.None);
            }
            _process.Dispose();
            _process = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        if (_heapProcess is { HasExited: false })
        {
            _heapProcess.Kill(entireProcessTree: true);
            await _heapProcess.WaitForExitAsync();
        }
        _heapProcess?.Dispose();
        _heapProcess = null;
    }

    public static async Task<string?> VersionAsync(ProfilingMode mode)
    {
        if (mode == ProfilingMode.None) return null;
        try
        {
            ProcessStartInfo start = QuietStart(ToolName(mode));
            start.ArgumentList.Add("--version");
            using Process process = Process.Start(start)!;
            string output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();
            return process.ExitCode == 0 ? output.Trim() : "unavailable";
        }
        catch { return "unavailable"; }
    }

    private static async Task EnsureToolAsync(string tool, CancellationToken cancellationToken)
    {
        ProcessStartInfo start = QuietStart(tool);
        start.ArgumentList.Add("--version");
        try
        {
            using Process process = Process.Start(start) ?? throw new InvalidOperationException();
            await process.WaitForExitAsync(cancellationToken);
            if (process.ExitCode != 0) throw new InvalidOperationException();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Profiling requested but '{tool}' is not installed or not on PATH.", e);
        }
    }

    private static ProcessStartInfo QuietStart(string file) => new(file)
    {
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    private static void Add(ProcessStartInfo start, params string[] args)
    {
        foreach (string arg in args) start.ArgumentList.Add(arg);
    }
}