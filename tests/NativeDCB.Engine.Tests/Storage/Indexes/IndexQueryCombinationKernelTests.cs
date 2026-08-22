using NativeDCB.Actors.Grains;
using NativeDCB.Actors.Messages;

namespace NativeDCB.Engine.Tests.Storage.Indexes;

public sealed class IndexQueryCombinationKernelTests
{
    private const string Database = "accounts";

    [Fact]
    public void IntersectsKeysAndTrimsEachSnapshotAtItsHead()
    {
        EventKeyMessage account = new("account", "a-1");
        EventKeyMessage region = new("region", "east");
        SequencedEventMessage first = Event(eventId: 1, "Deposited");
        SequencedEventMessage second = Event(eventId: 2, "Deposited");
        SequencedEventMessage beyondStaleHead = Event(eventId: 3, "Deposited");
        EventQueryMessage query = new([new QueryItemMessage(["Deposited"], [account, region])]);
        Dictionary<string, IndexSnapshotMessage> snapshots = new(StringComparer.Ordinal)
        {
            [IndexIdentity.Encode(Database, "Deposited", account)] =
                new IndexSnapshotMessage(true, Head: 4, [first, second, beyondStaleHead], Fault: null),
            [IndexIdentity.Encode(Database, "Deposited", region)] =
                new IndexSnapshotMessage(true, Head: 2, [second, beyondStaleHead], Fault: null)
        };

        Dictionary<long, SequencedEventMessage> result = IndexQueryCombinationKernel.Combine(
            Database, query, snapshots, throughEventIdInclusive: 10);

        SequencedEventMessage match = Assert.Single(result).Value;
        Assert.Same(second, match);
    }

    [Fact]
    public void UnionsTypesAndItemsAndDeduplicatesByEventId()
    {
        EventKeyMessage account = new("account", "a-1");
        EventKeyMessage region = new("region", "east");
        SequencedEventMessage opened = Event(eventId: 1, "Opened");
        SequencedEventMessage corrected = Event(eventId: 2, "Corrected");
        EventQueryMessage query = new(
        [
            new QueryItemMessage(["Opened", "Corrected"], [account]),
            new QueryItemMessage(["Opened"], [region])
        ]);
        Dictionary<string, IndexSnapshotMessage> snapshots = new(StringComparer.Ordinal)
        {
            [IndexIdentity.Encode(Database, "Opened", account)] =
                new IndexSnapshotMessage(true, Head: 3, [opened], Fault: null),
            [IndexIdentity.Encode(Database, "Corrected", account)] =
                new IndexSnapshotMessage(true, Head: 3, [corrected], Fault: null),
            [IndexIdentity.Encode(Database, "Opened", region)] =
                new IndexSnapshotMessage(true, Head: 3, [opened], Fault: null)
        };

        Dictionary<long, SequencedEventMessage> result = IndexQueryCombinationKernel.Combine(
            Database, query, snapshots, throughEventIdInclusive: 3);

        Assert.Equal([1L, 2L], result.Keys.Order());
        Assert.Same(opened, result[1]);
        Assert.Same(corrected, result[2]);
    }

    private static SequencedEventMessage Event(long eventId, string type)
    {
        return new SequencedEventMessage(
            eventId,
            type,
            DataJson: "{}",
            Keys: [],
            SchemaVersion: 1,
            DateTimeOffset.UnixEpoch,
            Guid.Empty,
            "TestCommand");
    }
}