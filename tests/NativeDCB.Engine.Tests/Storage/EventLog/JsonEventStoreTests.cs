using System.Text.Json;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Model.Queries;

namespace NativeDCB.Engine.Tests.Storage.EventLog;

public sealed class JsonEventStoreTests
{
    [Fact]
    public async Task CreateAppendAndReadRangePersistCommittedNdjson()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        await using JsonEventStore store = await JsonEventStore.CreateAsync(options);
        List<long> notifications = new();
        store.EventCommitted += @event => notifications.Add(@event.EventId);

        Guid commandId = Guid.NewGuid();
        AppendResult result = await store.AppendAsync(
            Batch(commandId, Event("account-opened", "account", "a1"), Event("money-deposited", "account", "a1")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        Assert.Equal(AppendOutcome.Committed, result.Outcome);
        Assert.Equal([1L, 2L], result.Events.Select(@event => @event.EventId));
        Assert.Equal(expected: 2, result.Head);
        Assert.Equal([1L, 2L], notifications);
        Assert.Equal(expected: 1, (await store.CommittedEvents.ReadAsync()).EventId);
        Assert.Equal(expected: 2, (await store.CommittedEvents.ReadAsync()).EventId);

        IReadOnlyList<SequencedEvent>
            range = await store.ReadRangeAsync(fromEventIdInclusive: 2, toEventIdInclusive: 2);
        SequencedEvent @event = Assert.Single(range);
        Assert.Equal("money-deposited", @event.Type);
        Assert.Equal(commandId, @event.CommandId);

        string partition = Path.Combine(directory.Path, "store_partition_000001_v1.json");
        await using FileStream partitionStream = new(
            partition,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using StreamReader reader = new(partitionStream);
        List<string> recordList = new();
        while (await reader.ReadLineAsync() is { } record)
        {
            recordList.Add(record);
        }

        string[] records = recordList.ToArray();
        Assert.Equal(expected: 4, records.Length);
        using JsonDocument header = JsonDocument.Parse(records[0]);
        using JsonDocument first = JsonDocument.Parse(records[1]);
        using JsonDocument commit = JsonDocument.Parse(records[3]);
        Assert.Equal("partition", header.RootElement.GetProperty("kind").GetString());
        Assert.Equal(store.StoreId, header.RootElement.GetProperty("storeId").GetGuid());
        Assert.Equal(expected: 1, header.RootElement.GetProperty("partitionNumber").GetInt32());
        Assert.Equal("event", first.RootElement.GetProperty("kind").GetString());
        Assert.Equal(expected: 1, first.RootElement.GetProperty("eventId").GetInt64());
        Assert.Equal("commit", commit.RootElement.GetProperty("kind").GetString());
        Assert.Equal(expected: 2, commit.RootElement.GetProperty("batchCount").GetInt32());
    }

    [Fact]
    public async Task AppendConditionConflictsOnlyWhenANewerEventMatchesQuery()
    {
        using TestDirectory directory = new();
        await using JsonEventStore store = await JsonEventStore.CreateAsync(new DatabaseOptions(directory.Path));
        await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("changed", "account", "a1")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        EventQuery a1 = Query("changed", "account", "a1");
        AppendResult conflict = await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("changed", "account", "a1")),
            new AppendCondition(a1, AfterEventId: 0));
        AppendResult unrelated = await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("changed", "account", "a2")),
            new AppendCondition(Query("changed", "account", "a2"), AfterEventId: 0));

        Assert.Equal(AppendOutcome.Conflict, conflict.Outcome);
        Assert.Empty(conflict.Events);
        Assert.Equal(expected: 1, conflict.Head);
        Assert.Equal(AppendOutcome.Committed, unrelated.Outcome);
        Assert.Equal(expected: 2, unrelated.Head);
    }

    [Fact]
    public async Task DuplicateCommandIdReturnsOriginalCommittedEvents()
    {
        using TestDirectory directory = new();
        await using JsonEventStore store = await JsonEventStore.CreateAsync(new DatabaseOptions(directory.Path));
        Guid commandId = Guid.NewGuid();
        AppendResult original = await store.AppendAsync(
            Batch(commandId, Event("original", "id", "1")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        AppendResult duplicate = await store.AppendAsync(
            Batch(commandId, Event("different", "id", "2")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        Assert.Equal(AppendOutcome.AlreadyCommitted, duplicate.Outcome);
        Assert.Equal(original.Events, duplicate.Events);
        Assert.Equal(expected: 1, duplicate.Head);
        Assert.Single(await store.ReadRangeAsync(fromEventIdInclusive: 1));
        Assert.Equal(original.Events, await store.ReadByCommandIdAsync(commandId));
    }

    [Fact]
    public async Task QueriesUnionItemsAndRequireAllKeysWithinAnItem()
    {
        using TestDirectory directory = new();
        await using JsonEventStore store = await JsonEventStore.CreateAsync(new DatabaseOptions(directory.Path));
        await store.AppendAsync(
            Batch(
                Guid.NewGuid(),
                Event("enrolled", ("student", "s1"), ("course", "c1")),
                Event("enrolled", ("student", "s2"), ("course", "c1")),
                Event("course-closed", ("course", "c2"))),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        EventQuery query = new(
        [
            new QueryItem(["enrolled"], [new EventKey("student", "s1"), new EventKey("course", "c1")]),
            new QueryItem(["course-closed"], [new EventKey("course", "c2")])
        ]);
        IReadOnlyList<SequencedEvent> matches = await store.ReadByQueryAsync(query);

        Assert.Equal([1L, 3L], matches.Select(@event => @event.EventId));
        Assert.Equal(expected: 3, (await store.ReadByQueryAsync(EventQuery.All)).Count);
        Assert.Empty(await store.ReadByCommandIdAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task PartitionReaderServesCommittedReadsWhileWriterOwnsTheDatabase()
    {
        using TestDirectory directory = new();
        await using JsonEventStore store = await JsonEventStore.CreateAsync(new DatabaseOptions(directory.Path));
        Guid commandId = Guid.NewGuid();
        await store.AppendAsync(
            Batch(commandId, Event("changed", "account", "a1")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        IReadOnlyList<SequencedEvent> range =
            await PartitionEventReader.ReadRangeAsync(directory.Path, fromEventIdInclusive: 1, toEventIdInclusive: 1);
        IReadOnlyList<SequencedEvent> queried = await PartitionEventReader.ReadByQueryAsync(
            directory.Path, Query("changed", "account", "a1"), store.Head);
        IReadOnlyList<SequencedEvent> command = await PartitionEventReader.ReadByCommandIdAsync(
            directory.Path, commandId, store.Head);
        IReadOnlyList<SequencedEvent> beforePromotion = await PartitionEventReader.ReadByCommandIdAsync(
            directory.Path, commandId, throughEventIdInclusive: 0);
        IReadOnlyList<PartitionStatus> partitions = await PartitionEventReader.ListPartitionStatusAsync(
            directory.Path, store.Head);
        IReadOnlyList<PartitionStatus> beforePromotionPartitions = await PartitionEventReader.ListPartitionStatusAsync(
            directory.Path, throughEventIdInclusive: 0);

        Assert.Equal(commandId, Assert.Single(range).CommandId);
        Assert.Single(queried);
        Assert.Single(command);
        Assert.Empty(beforePromotion);
        Assert.Equal(expected: 1, Assert.Single(partitions).CommittedEventCount);
        Assert.Equal(expected: 0, Assert.Single(beforePromotionPartitions).CommittedEventCount);
    }

    [Fact]
    public async Task PartitionReaderIgnoresAnIncompleteActiveAppendWithoutTakingTheWriterLock()
    {
        using TestDirectory directory = new();
        JsonEventStore store = await JsonEventStore.CreateAsync(new DatabaseOptions(directory.Path));
        await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("committed", "account", "a1")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));
        await store.DisposeAsync();

        string partition = Path.Combine(directory.Path, "store_partition_000001_v1.json");
        await using FileStream writer = new(
            partition,
            FileMode.Open,
            FileAccess.Write,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous);
        writer.Position = writer.Length;
        await writer.WriteAsync("{\"kind\":"u8.ToArray());
        await writer.FlushAsync();

        IReadOnlyList<SequencedEvent> events =
            await PartitionEventReader.ReadRangeAsync(directory.Path, fromEventIdInclusive: 1, toEventIdInclusive: 1);

        Assert.Equal("committed", Assert.Single(events).Type);
    }

    [Fact]
    public async Task SecondWriterCannotOpenUntilFirstReleasesLifetimeLock()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        JsonEventStore first = await JsonEventStore.CreateAsync(options);

        await Assert.ThrowsAsync<IOException>(() => JsonEventStore.OpenAsync(options));
        await first.DisposeAsync();

        await using JsonEventStore reopened = await JsonEventStore.OpenAsync(options);
        Assert.Equal(expected: 0, reopened.Head);
        await Assert.ThrowsAsync<InvalidOperationException>(() => JsonEventStore.CreateAsync(options));
    }

    [Fact]
    public async Task PartitionRolloverNeverSplitsABatch()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path) { MaxEventCountPerPartition = 3 };
        await using JsonEventStore store = await JsonEventStore.CreateAsync(options);
        await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("one", "id", "1"), Event("two", "id", "2")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));
        await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("three", "id", "3"), Event("four", "id", "4")),
            new AppendCondition(EventQuery.All, AfterEventId: 2));

        IReadOnlyList<PartitionStatus> partitions = await store.ListPartitionStatusAsync();
        Assert.Equal(expected: 2, partitions.Count);
        Assert.Equal([2, 2], partitions.Select(partition => partition.CommittedEventCount));
        Assert.False(partitions[index: 0].IsActive);
        Assert.True(partitions[index: 1].IsActive);
        Assert.Equal((1L, 2L), (partitions[index: 0].FirstEventId, partitions[index: 0].LastEventId));
        Assert.Equal((3L, 4L), (partitions[index: 1].FirstEventId, partitions[index: 1].LastEventId));
    }

    [Fact]
    public async Task BatchLargerThanLimitGetsItsOwnClosedPartition()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path) { MaxEventCountPerPartition = 2 };
        await using JsonEventStore store = await JsonEventStore.CreateAsync(options);

        await store.AppendAsync(
            Batch(Guid.NewGuid(), Event("one", "id", "1"), Event("two", "id", "2"), Event("three", "id", "3")),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        IReadOnlyList<PartitionStatus> partitions = await store.ListPartitionStatusAsync();
        Assert.Equal(expected: 2, partitions.Count);
        Assert.Equal(expected: 3, partitions[index: 0].CommittedEventCount);
        Assert.Equal(expected: 0, partitions[index: 1].CommittedEventCount);
        Assert.True(partitions[index: 1].IsActive);
    }

    private static EventBatch Batch(Guid commandId, params CandidateEvent[] events)
    {
        return new EventBatch(commandId, "test-command", events);
    }

    private static CandidateEvent Event(string type, string keyName, string keyValue)
    {
        return Event(type, (keyName, keyValue));
    }

    private static CandidateEvent Event(string type, params (string Name, string Value)[] keys)
    {
        return new CandidateEvent(
            type,
            JsonSerializer.SerializeToElement(new { value = type }),
            keys.Select(key => new EventKey(key.Name, key.Value)).ToArray());
    }

    private static EventQuery Query(string type, string keyName, string keyValue)
    {
        return new EventQuery([new QueryItem([type], [new EventKey(keyName, keyValue)])]);
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