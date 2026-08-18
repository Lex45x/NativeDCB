using System.Text.Json;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Engine.Storage.State;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Model.Queries;

namespace NativeDCB.Engine.Tests.Storage.State;

public sealed class StateBuilderTests
{
    [Fact]
    public async Task BuildsCumulativeStateForClosedPartition()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path) { MaxEventCountPerPartition = 2 };
        Guid firstCommand = Guid.NewGuid();
        await using JsonEventStore store = await JsonEventStore.CreateAsync(options);
        await store.AppendAsync(
            new EventBatch(
                firstCommand,
                "first-command",
                [Event("opened", "a1"), Event("updated", "a1")]),
            new AppendCondition(EventQuery.All, AfterEventId: 0));

        StateBuilder builder = new(options);
        StateFileModel state = await builder.BuildAsync(partitionNumber: 1);

        Assert.Equal(expected: 1, state.FormatVersion);
        Assert.Equal(store.StoreId, state.StoreId);
        Assert.Equal(expected: 1, state.PartitionNumber);
        Assert.Equal(expected: 2, state.Head);
        Assert.Equal(expected: 2, state.CommittedEventCount);
        Assert.Equal([1L, 2L], state.Events.Select(value => value.EventId));
        Assert.Equal([1L, 2L], state.KnownEventIds);
        CommandState command = Assert.Single(state.Commands);
        Assert.Equal(firstCommand, command.CommandId);
        Assert.Equal((1L, 2L, 2), (command.FirstEventId, command.LastEventId, command.EventCount));
        Assert.Equal(expected: 2, state.LatestEvents.Count);
        PartitionCheckpoint partition = Assert.Single(state.Partitions);
        Assert.Equal((1, 2), (partition.PartitionNumber, partition.CommittedEventCount));

        string path = Path.Combine(directory.Path, "state_000001_v1.json");
        Assert.True(File.Exists(path));
        await using FileStream stream = File.OpenRead(path);
        JsonSerializerOptions jsonOptions = new() { PropertyNameCaseInsensitive = true };
        StateFileModel? persisted = await JsonSerializer.DeserializeAsync<StateFileModel>(stream, jsonOptions);
        Assert.NotNull(persisted);
        Assert.Equal(state.StoreId, persisted.StoreId);
        Assert.Equal(state.Head, persisted.Head);
        Assert.Equal(state.Events.Select(value => value.EventId), persisted.Events.Select(value => value.EventId));
        Assert.Equal(state.KnownEventIds, persisted.KnownEventIds);
        Assert.Equal(state.Commands, persisted.Commands);
        Assert.Equal(state.LatestEvents, persisted.LatestEvents);
        Assert.Equal(state.Partitions, persisted.Partitions);
    }

    [Fact]
    public async Task RefusesStateForActivePartition()
    {
        using TestDirectory directory = new();
        DatabaseOptions options = new(directory.Path);
        await using JsonEventStore store = await JsonEventStore.CreateAsync(options);
        StateBuilder builder = new(options);

        await Assert.ThrowsAsync<InvalidOperationException>(() => builder.BuildAsync(partitionNumber: 1));
    }

    private static CandidateEvent Event(string type, string id)
    {
        return new CandidateEvent(type, JsonSerializer.SerializeToElement(new { id }), [new EventKey("entity", id)]);
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