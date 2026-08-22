using BenchmarkDotNet.Attributes;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.MicroBenchmarks.Infrastructure;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.MicroBenchmarks.Persistence;

[Config(typeof(FilesystemBenchmarkConfig))]
[BenchmarkCategory("Filesystem")]
public class PartitionReadBenchmarks
{
    private const int BatchesPerPartition = 4;
    private Guid _firstCommandId;
    private StorageFixture _fixture = null!;
    private Guid _lastCommandId;
    private Guid _middleCommandId;
    private Guid _missingCommandId;
    private string _workingPath = null!;

    [Params(64, 1000)]
    public int History { get; set; }

    [Params(1, 4)]
    public int ClosedPartitions { get; set; }

    [Params(128)]
    public int PayloadBytes { get; set; }

    public IEnumerable<int> RangeEventCounts()
    {
        yield return 1;
        yield return 64;
    }

    public IEnumerable<int> MaxCounts() => [1, 64];

    public IEnumerable<QueryCase> QueryCases()
    {
        yield return new QueryCase("Selectivity=100%", EventQuery.All);
        yield return new QueryCase(
            "Selectivity=10%",
            new EventQuery([new QueryItem(["selected"], [])]));
        yield return new QueryCase(
            "Selectivity=Single",
            new EventQuery([new QueryItem([], [new EventKey("key-0", "value-00000000-0")])]));
        yield return new QueryCase(
            "Selectivity=0%",
            new EventQuery([new QueryItem([], [new EventKey("key-0", "missing")])]));
    }

    public IEnumerable<QuerySnapshotCase> QuerySnapshotCases()
    {
        foreach (QueryCase query in QueryCases().Take(2))
        foreach (int maxCount in MaxCounts())
        {
            yield return new QuerySnapshotCase(
                $"{query.Shape},MaxCount={maxCount}",
                query.Query,
                maxCount);
        }
    }

    [GlobalSetup]
    public async Task GlobalSetup()
    {
        _fixture = new StorageFixture(nameof(PartitionReadBenchmarks));
        await BenchmarkFixtures.SeedStoreAsync(
            _fixture.TemplatePath,
            History,
            ClosedPartitions,
            payloadBytes: PayloadBytes,
            batchesPerPartition: BatchesPerPartition,
            eventType: index => index % 10 == 0 ? "selected" : "other");
        int batchCount = ClosedPartitions * BatchesPerPartition;
        _firstCommandId = BenchmarkFixtures.GuidFor(1);
        _middleCommandId = BenchmarkFixtures.GuidFor((batchCount / 2) + 1);
        _lastCommandId = BenchmarkFixtures.GuidFor(batchCount);
        _missingCommandId = BenchmarkFixtures.GuidFor(batchCount + 1000);
        Console.WriteLine(_fixture.DescribeEnvironment());
    }

    [IterationSetup]
    public void IterationSetup() => _workingPath = _fixture.CreateWorkingDirectory();

    [Benchmark]
    public Task<long> ReadHead() => PartitionEventReader.GetHeadAsync(_workingPath);

    [Benchmark]
    [ArgumentsSource(nameof(RangeEventCounts))]
    public Task<IReadOnlyList<SequencedEvent>> ReadRange(int eventCount) => PartitionEventReader.ReadRangeAsync(
        _workingPath, fromEventIdInclusive: 1, toEventIdInclusive: eventCount);

    [Benchmark]
    [ArgumentsSource(nameof(QueryCases))]
    public Task<IReadOnlyList<SequencedEvent>> ReadQuery(QueryCase input) =>
        PartitionEventReader.ReadByQueryAsync(_workingPath, input.Query, throughEventIdInclusive: History);

    [Benchmark]
    public Task<IReadOnlyList<SequencedEvent>> ReadCommandFirst() => ReadCommand(_firstCommandId);

    [Benchmark]
    public Task<IReadOnlyList<SequencedEvent>> ReadCommandMiddle() => ReadCommand(_middleCommandId);

    [Benchmark]
    public Task<IReadOnlyList<SequencedEvent>> ReadCommandLast() => ReadCommand(_lastCommandId);

    [Benchmark]
    public Task<IReadOnlyList<SequencedEvent>> ReadCommandMiss() => ReadCommand(_missingCommandId);

    [Benchmark]
    [ArgumentsSource(nameof(MaxCounts))]
    public Task<EventReadSnapshot> ReadSnapshot(int maxCount) => PartitionEventReader.ReadRangeSnapshotAsync(
        _workingPath, afterEventIdExclusive: 0, throughEventIdInclusive: History, maxCount: maxCount);

    [Benchmark]
    [ArgumentsSource(nameof(QuerySnapshotCases))]
    public Task<EventReadSnapshot> ReadQuerySnapshot(QuerySnapshotCase input) =>
        PartitionEventReader.ReadQuerySnapshotAsync(
            _workingPath,
            input.Query,
            afterEventIdExclusive: 0,
            throughEventIdInclusive: History,
            maxCount: input.MaxCount);

    [IterationCleanup]
    public void IterationCleanup() => _fixture.DeleteWorkingDirectory();

    [GlobalCleanup]
    public void GlobalCleanup() => _fixture.Dispose();

    private Task<IReadOnlyList<SequencedEvent>> ReadCommand(Guid commandId) =>
        PartitionEventReader.ReadByCommandIdAsync(
            _workingPath, commandId, throughEventIdInclusive: History);

    public sealed record QueryCase(string Shape, EventQuery Query)
    {
        public override string ToString() => Shape;
    }

    public sealed record QuerySnapshotCase(string Shape, EventQuery Query, int MaxCount)
    {
        public override string ToString() => Shape;
    }
}
