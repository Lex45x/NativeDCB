using BenchmarkDotNet.Attributes;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.MicroBenchmarks.Infrastructure;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Model.Queries;

namespace NativeDCB.MicroBenchmarks.Persistence;

[Config(typeof(FilesystemBenchmarkConfig))]
[BenchmarkCategory("Filesystem")]
public class AppendBenchmarks
{
    private EventBatch _batch = null!;
    private AppendCondition _condition = null!;
    private StorageFixture _fixture = null!;
    private JsonEventStore? _store;

    [Params(1, 8)]
    public int BatchCount { get; set; }

    [Params(128, 1024)]
    public int PayloadBytes { get; set; }

    [Params(1, 4)]
    public int Keys { get; set; }

    [Params(64, 256)]
    public int ExistingHead { get; set; }

    [Params("None", "First", "Last")]
    public string ConflictPosition { get; set; } = null!;

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _fixture = new StorageFixture(nameof(AppendBenchmarks));
        await BenchmarkFixtures.SeedStoreAsync(_fixture.TemplatePath, ExistingHead, payloadBytes: 128);
        Console.WriteLine(_fixture.DescribeEnvironment());
    }

    [IterationSetup]
    public void IterationSetup()
    {
        string directory = _fixture.CreateWorkingDirectory();
        _store = JsonEventStore.OpenAsync(new DatabaseOptions(directory)).GetAwaiter().GetResult();
        _batch = BenchmarkFixtures.Batch(ordinal: 1_000_000, BatchCount, PayloadBytes, Keys);
        long matchOrdinal = ConflictPosition == "Last" ? ExistingHead - 1L : 0;
        EventQuery conflictQuery = new(
        [
            new QueryItem(
                ["changed"],
                [new EventKey("key-0", $"value-{matchOrdinal:D8}-0")])
        ]);
        _condition = new AppendCondition(
            ConflictPosition == "None" ? EventQuery.All : conflictQuery,
            ConflictPosition == "None" ? _store.Head : 0);
    }

    [Benchmark]
    public Task<AppendResult> ConditionalDurableAppend() => _store!.AppendAsync(_batch, _condition);

    [IterationCleanup]
    public void IterationCleanup()
    {
        if (_store is not null)
        {
            _store.DisposeAsync().AsTask().GetAwaiter().GetResult();
            _store = null;
        }

        _fixture.DeleteWorkingDirectory();
    }

    [GlobalCleanup]
    public void GlobalCleanup() => _fixture.Dispose();
}
