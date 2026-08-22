using BenchmarkDotNet.Attributes;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.State;
using NativeDCB.MicroBenchmarks.Infrastructure;

namespace NativeDCB.MicroBenchmarks.Persistence;

[Config(typeof(FilesystemBenchmarkConfig))]
[BenchmarkCategory("Filesystem")]
public class RecoveryBenchmarks
{
    private StorageFixture _fixture = null!;
    private JsonEventStore? _opened;
    private string _workingPath = null!;

    [Params(64, 1000)]
    public int Events { get; set; }

    [Params(1, 4)]
    public int ClosedPartitions { get; set; }

    [Params(128)]
    public int PayloadBytes { get; set; }

    [Params("Absent", "Valid", "Invalid")]
    public string Checkpoint { get; set; } = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _fixture = new StorageFixture(nameof(RecoveryBenchmarks));
        await BenchmarkFixtures.SeedStoreAsync(
            _fixture.TemplatePath, Events, ClosedPartitions, payloadBytes: PayloadBytes);
        if (Checkpoint == "Valid")
        {
            await new StateBuilder(new DatabaseOptions(_fixture.TemplatePath))
                .BuildAsync(ClosedPartitions);
        }
        else if (Checkpoint == "Invalid")
        {
            await File.WriteAllTextAsync(
                Path.Combine(_fixture.TemplatePath, $"state_{ClosedPartitions:000000}_v1.json"),
                "{\"formatVersion\":1,\"storeId\":\"invalid\"}");
        }

        Console.WriteLine(_fixture.DescribeEnvironment());
    }

    [IterationSetup]
    public void IterationSetup()
    {
        _workingPath = _fixture.CreateWorkingDirectory();
    }

    [Benchmark]
    public async Task<long> ColdWriterOpenAndRecovery()
    {
        try
        {
            _opened = await JsonEventStore.OpenAsync(new DatabaseOptions(_workingPath));
            return _opened.Head;
        }
        catch (InvalidDataException)
        {
            return -1;
        }
    }

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_opened is not null)
        {
            _opened.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _opened = null;
        }

        _fixture.DeleteWorkingDirectory();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _fixture.Dispose();
}
