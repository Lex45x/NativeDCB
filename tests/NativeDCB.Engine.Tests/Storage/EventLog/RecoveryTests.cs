using System.Text;
using System.Text.Json;

using NativeDCB.Model;

namespace NativeDCB.Engine.Tests;

public sealed class RecoveryTests
{
    [Fact]
    public async Task ReopenRecoversMultiEventBatchAndContinuesGlobalOrdering()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        Guid firstCommand = Guid.NewGuid();
        await using (JsonEventStore created = await JsonEventStore.CreateAsync(options))
        {
            await created.AppendAsync(
                Batch(firstCommand, Event("one"), Event("two"), Event("three")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
        }

        await using JsonEventStore reopened = await JsonEventStore.OpenAsync(options);
        Assert.Equal(expected: 3, reopened.Head);
        Assert.Equal([1L, 2L, 3L],
            (await reopened.ReadByCommandIdAsync(firstCommand)).Select(@event => @event.EventId));

        AppendResult next = await reopened.AppendAsync(
            Batch(Guid.NewGuid(), Event("four")),
            new AppendCondition(EventQuery.All, AfterEventId: 3));
        Assert.Equal(expected: 4, Assert.Single(next.Events).EventId);
    }

    [Fact]
    public async Task ReopenUsesValidatedClosedPartitionStateAndReplaysTheActiveTail()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path) { MaxEventCountPerPartition = 2 };
        Guid firstCommand = Guid.NewGuid();
        Guid secondCommand = Guid.NewGuid();
        await using (JsonEventStore created = await JsonEventStore.CreateAsync(options))
        {
            await created.AppendAsync(
                Batch(firstCommand, Event("one"), Event("two")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
            await created.AppendAsync(
                Batch(secondCommand, Event("three")),
                new AppendCondition(EventQuery.All, AfterEventId: 2));
            await new StateBuilder(options).BuildAsync(partitionNumber: 1);
        }

        await using JsonEventStore reopened = await JsonEventStore.OpenAsync(options);

        Assert.Equal(expected: 3, reopened.Head);
        Assert.Equal([1L, 2L], (await reopened.ReadByCommandIdAsync(firstCommand)).Select(value => value.EventId));
        Assert.Equal(expected: 3, Assert.Single(await reopened.ReadByCommandIdAsync(secondCommand)).EventId);
        Assert.Equal(["one", "two", "three"],
            (await reopened.ReadRangeAsync(fromEventIdInclusive: 1)).Select(value => value.Type));
    }

    [Fact]
    public async Task ReopenIgnoresStateWithAModifiedEventSnapshot()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path) { MaxEventCountPerPartition = 1 };
        await using (JsonEventStore created = await JsonEventStore.CreateAsync(options))
        {
            await created.AppendAsync(Batch(Guid.NewGuid(), Event("one")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
            await created.AppendAsync(Batch(Guid.NewGuid(), Event("two")),
                new AppendCondition(EventQuery.All, AfterEventId: 1));
            await new StateBuilder(options).BuildAsync(partitionNumber: 1);
        }

        string statePath = Path.Combine(directory.Path, "state_000001_v1.json");
        string state = await File.ReadAllTextAsync(statePath);
        await File.WriteAllTextAsync(statePath, state.Replace("\"type\":\"one\"", "\"type\":\"tampered\""));

        await using JsonEventStore reopened = await JsonEventStore.OpenAsync(options);

        Assert.Equal(["one", "two"],
            (await reopened.ReadRangeAsync(fromEventIdInclusive: 1)).Select(value => value.Type));
    }

    [Fact]
    public async Task ReopenIgnoresAndTruncatesCompleteUncommittedFinalBatch()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        await using (JsonEventStore created = await JsonEventStore.CreateAsync(options))
        {
            await created.AppendAsync(
                Batch(Guid.NewGuid(), Event("committed")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
        }

        string partition = Path.Combine(directory.Path, "store_partition_000001_v1.json");
        long committedLength = new FileInfo(partition).Length;
        string uncommitted = JsonSerializer.Serialize(new
        {
            kind = "event",
            eventId = 2,
            batchId = Guid.NewGuid(),
            batchIndex = 0,
            batchCount = 2,
            type = "not-committed",
            schemaVersion = 1,
            keys = new[] { new { name = "id", value = "not-committed" } },
            data = new { },
            timestampUtc = DateTimeOffset.UtcNow,
            commandId = Guid.NewGuid(),
            commandType = "test"
        });
        await File.AppendAllTextAsync(partition, uncommitted + "\n", Encoding.UTF8);

        await using JsonEventStore reopened = await JsonEventStore.OpenAsync(options);
        Assert.Equal(expected: 1, reopened.Head);
        Assert.Equal(committedLength, new FileInfo(partition).Length);
        Assert.Single(await reopened.ReadRangeAsync(fromEventIdInclusive: 1));
    }

    [Fact]
    public async Task ReopenIgnoresTruncatedFinalJsonObject()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        await using (JsonEventStore created = await JsonEventStore.CreateAsync(options))
        {
            await created.AppendAsync(
                Batch(Guid.NewGuid(), Event("committed")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
        }

        string partition = Path.Combine(directory.Path, "store_partition_000001_v1.json");
        long committedLength = new FileInfo(partition).Length;
        await File.AppendAllTextAsync(partition, "{\"kind\":\"event\",\"eventId\":2", Encoding.UTF8);

        await using JsonEventStore reopened = await JsonEventStore.OpenAsync(options);
        Assert.Equal(expected: 1, reopened.Head);
        Assert.Equal(committedLength, new FileInfo(partition).Length);
    }

    [Fact]
    public async Task ReopenRejectsCorruptRecordAfterCommittedPrefix()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        await using (JsonEventStore created = await JsonEventStore.CreateAsync(options))
        {
            await created.AppendAsync(
                Batch(Guid.NewGuid(), Event("committed")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
        }

        string partition = Path.Combine(directory.Path, "store_partition_000001_v1.json");
        await File.AppendAllTextAsync(partition, "{\"kind\":\"unknown\"}\n", Encoding.UTF8);

        await Assert.ThrowsAsync<InvalidDataException>(() => JsonEventStore.OpenAsync(options));
    }

    [Fact]
    public async Task ReopenRejectsPartitionCopiedFromAnotherDatabase()
    {
        using TestDirectory firstDirectory = new();
        using TestDirectory secondDirectory = new();
        DatabaseOptions firstOptions = new(firstDirectory.Path);
        DatabaseOptions secondOptions = new(secondDirectory.Path);
        await using (JsonEventStore first = await JsonEventStore.CreateAsync(firstOptions))
        {
            await first.AppendAsync(
                Batch(Guid.NewGuid(), Event("first")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
        }

        await using (JsonEventStore second = await JsonEventStore.CreateAsync(secondOptions))
        {
            await second.AppendAsync(
                Batch(Guid.NewGuid(), Event("second")),
                new AppendCondition(EventQuery.All, AfterEventId: 0));
        }

        File.Copy(
            Path.Combine(secondDirectory.Path, "store_partition_000001_v1.json"),
            Path.Combine(firstDirectory.Path, "store_partition_000001_v1.json"),
            overwrite: true);

        await Assert.ThrowsAsync<InvalidDataException>(() => JsonEventStore.OpenAsync(firstOptions));
    }

    private static EventBatch Batch(Guid commandId, params CandidateEvent[] events)
    {
        return new EventBatch(commandId, "test-command", events);
    }

    private static CandidateEvent Event(string type)
    {
        return new CandidateEvent(type, JsonSerializer.SerializeToElement(new { type }), [new EventKey("id", type)]);
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "NativeDCB.Tests",
            Guid.NewGuid().ToString("N"));

        public void Dispose()
        {
            if (Directory.Exists(Path))
            {
                Directory.Delete(Path, recursive: true);
            }
        }
    }
}