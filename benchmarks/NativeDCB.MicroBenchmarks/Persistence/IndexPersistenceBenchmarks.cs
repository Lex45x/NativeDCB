using BenchmarkDotNet.Attributes;

using NativeDCB.Engine.Storage.Indexes;
using NativeDCB.Engine.Storage.State;
using NativeDCB.MicroBenchmarks.Infrastructure;
using NativeDCB.Model.Events;

namespace NativeDCB.MicroBenchmarks.Persistence;

[Config(typeof(FilesystemBenchmarkConfig))]
[BenchmarkCategory("Filesystem")]
public class IndexPersistenceBenchmarks
{
    private const string PublishEventType = "changed-publish";
    private readonly EventKey _key = new("entity", "benchmark");
    private StorageFixture _fixture = null!;
    private IndexFileModel _model = null!;
    private string _workingPath = null!;

    [Params(64, 1000)]
    public int IndexedEvents { get; set; }

    [Params(128, 1024)]
    public int PayloadBytes { get; set; }

    public IEnumerable<int> PublicationGenerations() => [1, 4];

    public IEnumerable<LoadCase> LoadCases() =>
    [
        new LoadCase("Manifest=Valid,Generations=1", "changed-load-1"),
        new LoadCase("Manifest=Valid,Generations=4", "changed-load-4"),
        new LoadCase("Manifest=Absent", "changed-load-absent")
    ];

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _fixture = new StorageFixture(nameof(IndexPersistenceBenchmarks));
        _model = CreateModel(PublishEventType);
        IndexFileModel singleGeneration = CreateModel("changed-load-1");
        await IndexFileStore.PublishAsync(_fixture.TemplatePath, singleGeneration, CancellationToken.None);
        IndexFileModel fourGenerations = CreateModel("changed-load-4");
        for (int generation = 0; generation < 4; generation++)
        {
            await IndexFileStore.PublishAsync(_fixture.TemplatePath, fourGenerations, CancellationToken.None);
        }

        Console.WriteLine(_fixture.DescribeEnvironment());
    }

    private IndexFileModel CreateModel(string eventType)
    {
        SequencedEvent[] events = Enumerable.Range(0, IndexedEvents)
            .Select(index => new SequencedEvent(
                index + 1L,
                eventType,
                BenchmarkFixtures.Payload(PayloadBytes, index),
                [_key],
                SchemaVersion: 1,
                BenchmarkFixtures.Timestamp,
                BenchmarkFixtures.GuidFor(index + 1),
                "benchmark-command"))
            .ToArray();
        return new IndexFileModel(
            FormatVersion: 1,
            eventType,
            _key,
            Head: IndexedEvents,
            events,
            StateBuilder.EventSchemaFingerprint,
            StateBuilder.KeyEncodingFingerprint);
    }

    [IterationSetup(Target = nameof(PublishGeneration))]
    public void SetupPublish() => _workingPath = _fixture.CreateWorkingDirectory(copyTemplate: false);

    [IterationSetup(Target = nameof(LoadPublishedGeneration))]
    public void SetupLoad() => _workingPath = _fixture.CreateWorkingDirectory();

    [Benchmark]
    [ArgumentsSource(nameof(PublicationGenerations))]
    public async Task<long> PublishGeneration(int generations)
    {
        for (int generation = 0; generation < generations; generation++)
        {
            await IndexFileStore.PublishAsync(_workingPath, _model, CancellationToken.None);
        }

        return _model.Head;
    }

    [Benchmark]
    [ArgumentsSource(nameof(LoadCases))]
    public async Task<object?> LoadPublishedGeneration(LoadCase input) =>
        await IndexFileStore.TryReadPublishedAsync(
            _workingPath, input.EventType, _key, CancellationToken.None);

    [IterationCleanup]
    public void IterationCleanup() => _fixture.DeleteWorkingDirectory();

    [GlobalCleanup]
    public void GlobalCleanup() => _fixture.Dispose();

    public sealed record LoadCase(string Shape, string EventType)
    {
        public override string ToString() => Shape;
    }
}
