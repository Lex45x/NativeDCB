using BenchmarkDotNet.Attributes;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.State;
using NativeDCB.MicroBenchmarks.Infrastructure;

namespace NativeDCB.MicroBenchmarks.State;

[Config(typeof(FilesystemBenchmarkConfig))]
[BenchmarkCategory("Filesystem")]
public class StateBuilderBenchmarks
{
    private StorageFixture _fixture = null!;
    private StateBuilder _builder = null!;

    [Params(64, 1000)]
    public int Events { get; set; }

    [Params(1, 4)]
    public int ClosedPartitions { get; set; }

    [Params(1, 4)]
    public int Keys { get; set; }

    [Params(128)]
    public int PayloadBytes { get; set; }

    [Params(false, true)]
    public bool PriorCheckpoint { get; set; }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _fixture = new StorageFixture(nameof(StateBuilderBenchmarks));
        await BenchmarkFixtures.SeedStoreAsync(
            _fixture.TemplatePath, Events, ClosedPartitions, payloadBytes: PayloadBytes, keyCount: Keys);
        Console.WriteLine(_fixture.DescribeEnvironment());
    }

    [IterationSetup]
    public void IterationSetup()
    {
        string working = _fixture.CreateWorkingDirectory();
        _builder = new StateBuilder(new DatabaseOptions(working));
        if (PriorCheckpoint)
        {
            _builder.BuildAsync(ClosedPartitions).GetAwaiter().GetResult();
        }
    }

    [Benchmark]
    public Task<StateFileModel> BuildCumulativeState() => _builder.BuildAsync(ClosedPartitions);

    [IterationCleanup]
    public void IterationCleanup() => _fixture.DeleteWorkingDirectory();

    [GlobalCleanup]
    public void GlobalCleanup() => _fixture.Dispose();
}
