using BenchmarkDotNet.Attributes;

using NativeDCB.Actors.Grains;
using NativeDCB.Actors.Messages;
using NativeDCB.MicroBenchmarks.Infrastructure;

namespace NativeDCB.MicroBenchmarks.Indexes;

[MemoryDiagnoser]
public class IndexCombinationBenchmarks
{
    private const string Database = "benchmark";
    private EventQueryMessage _query = null!;
    private IReadOnlyDictionary<string, IndexSnapshotMessage> _snapshots = null!;
    private long _through;

    [Params(1, 4)]
    public int SnapshotCount { get; set; }

    [Params(64, 1000)]
    public int Events { get; set; }

    [Params("Intersection", "TypeUnion", "ItemUnion", "Dedup")]
    public string QueryShape { get; set; } = null!;

    [Params("Full", "Half", "None")]
    public string Overlap { get; set; } = null!;

    [Params(false, true)]
    public bool StaleHeads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        Dictionary<string, IndexSnapshotMessage> snapshots = new(StringComparer.Ordinal);
        QueryItemMessage[] items = QueryShape switch
        {
            "TypeUnion" => [Item(itemIndex: 0, eventTypeCount: 4)],
            "ItemUnion" => Enumerable.Range(0, 4)
                .Select(itemIndex => Item(itemIndex, eventTypeCount: 1))
                .ToArray(),
            "Dedup" => Enumerable.Range(0, 4)
                .Select(_ => Item(itemIndex: 0, eventTypeCount: 1))
                .ToArray(),
            _ => [Item(itemIndex: 0, eventTypeCount: 1)]
        };
        _query = new EventQueryMessage(items);
        _snapshots = snapshots;
        _through = QueryShape is "TypeUnion" or "ItemUnion" ? 3_000_000L + Events : Events;

        QueryItemMessage Item(int itemIndex, int eventTypeCount)
        {
            string[] eventTypes = Enumerable.Range(0, eventTypeCount)
                .Select(typeIndex => $"type-{itemIndex}-{typeIndex}")
                .ToArray();
            EventKeyMessage[] keys = Enumerable.Range(0, SnapshotCount)
                .Select(keyIndex => new EventKeyMessage($"key-{keyIndex}", $"value-{itemIndex}"))
                .ToArray();
            foreach ((string eventType, int typeIndex) in eventTypes.Select((value, index) => (value, index)))
            foreach ((EventKeyMessage key, int keyIndex) in keys.Select((value, index) => (value, index)))
            {
                int sourceIndex = QueryShape == "TypeUnion" ? typeIndex : itemIndex;
                SequencedEventMessage[] events = Enumerable.Range(0, Events)
                    .Where(eventIndex => Included(eventIndex, keyIndex))
                    .Select(eventIndex => Message(sourceIndex, eventIndex, eventType, keys))
                    .ToArray();
                long idBase = sourceIndex * 1_000_000L;
                long head = idBase + (StaleHeads && keyIndex == SnapshotCount - 1 ? Events / 2 : Events);
                snapshots[IndexIdentity.Encode(Database, eventType, key)] =
                    new IndexSnapshotMessage(Supported: true, head, events, Fault: null);
            }

            return new QueryItemMessage(eventTypes, keys);
        }
    }

    [Benchmark]
    public object Combine() => IndexQueryCombinationKernel.Combine(Database, _query, _snapshots, _through);

    private bool Included(int eventIndex, int keyIndex) => Overlap switch
    {
        "Full" => true,
        "Half" => keyIndex == 0 || eventIndex % 2 == 0,
        _ => keyIndex == 0 || eventIndex % SnapshotCount == keyIndex
    };

    private static SequencedEventMessage Message(
        int itemIndex,
        int eventIndex,
        string eventType,
        EventKeyMessage[] keys)
    {
        long eventId = (itemIndex * 1_000_000L) + eventIndex + 1;
        return new SequencedEventMessage(
            eventId,
            eventType,
            "{\"value\":1}",
            keys,
            SchemaVersion: 1,
            BenchmarkFixtures.Timestamp,
            BenchmarkFixtures.GuidFor(checked((int)(eventId % int.MaxValue))),
            "benchmark-command");
    }
}
