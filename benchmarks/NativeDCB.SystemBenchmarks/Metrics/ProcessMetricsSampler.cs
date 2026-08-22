using System.Diagnostics;
using System.Globalization;

namespace NativeDCB.SystemBenchmarks.Metrics;

internal sealed class ProcessMetricsSampler : IAsyncDisposable
{
    private readonly Func<Process?> _getProcess;
    private readonly StreamWriter _writer;
    private readonly CancellationTokenSource _stop = new();
    private Task? _task;
    private bool _disposed;

    public ProcessMetricsSampler(Func<Process?> getProcess, string path)
    {
        _getProcess = getProcess;
        _writer = new StreamWriter(new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read));
    }

    public async Task StartAsync()
    {
        await _writer.WriteLineAsync("utc,pid,cpu_total_ms,cpu_normalized_percent,working_set_bytes,private_bytes,threads,exited");
        await _writer.FlushAsync();
        _task = SampleAsync();
    }

    private async Task SampleAsync()
    {
        Process? observed = null;
        TimeSpan previousCpu = TimeSpan.Zero;
        long previousStamp = 0;
        int previousPid = 0;
        int exitedPid = 0;
        int trackedPid = 0;
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Process? current = _getProcess();
                if (current is not null)
                {
                    if (trackedPid != 0 && current.Id != trackedPid && exitedPid != trackedPid)
                    {
                        exitedPid = trackedPid;
                        await _writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O},{trackedPid},,,,,,true");
                        await _writer.FlushAsync();
                    }
                    observed = current;
                    trackedPid = current.Id;
                }
                if (observed is not null)
                {
                    observed.Refresh();
                    if (observed.HasExited)
                    {
                        if (exitedPid != observed.Id)
                        {
                            exitedPid = observed.Id;
                            await _writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O},{observed.Id},,,,,,true");
                            await _writer.FlushAsync();
                        }
                    }
                    else
                    {
                        TimeSpan cpu = observed.TotalProcessorTime;
                        long now = Stopwatch.GetTimestamp();
                        string normalized = "";
                        if (previousStamp != 0 && previousPid == observed.Id)
                        {
                            double wallMs = Stopwatch.GetElapsedTime(previousStamp, now).TotalMilliseconds;
                            double value = wallMs == 0 ? 0 :
                                (cpu - previousCpu).TotalMilliseconds / wallMs / Environment.ProcessorCount * 100;
                            normalized = value.ToString("F3", CultureInfo.InvariantCulture);
                        }
                        if (exitedPid == observed.Id) exitedPid = 0;
                        await _writer.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
                            $"{DateTimeOffset.UtcNow:O},{observed.Id},{cpu.TotalMilliseconds:F3},{normalized},{observed.WorkingSet64},{observed.PrivateMemorySize64},{observed.Threads.Count},false"));
                        await _writer.FlushAsync();
                        previousCpu = cpu;
                        previousStamp = now;
                        previousPid = observed.Id;
                    }
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or ObjectDisposedException) { }
            try { await Task.Delay(TimeSpan.FromSeconds(1), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _stop.Cancel();
        if (_task is not null) await _task;
        await _writer.DisposeAsync();
        _stop.Dispose();
    }
}