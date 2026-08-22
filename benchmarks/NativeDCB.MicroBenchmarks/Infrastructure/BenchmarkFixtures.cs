using System.Text.Json;
using System.Text;

using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Model.Queries;

namespace NativeDCB.MicroBenchmarks.Infrastructure;

internal static class BenchmarkFixtures
{
    public static readonly DateTimeOffset Timestamp = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static Guid GuidFor(int value)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, value);
        return new Guid(bytes);
    }

    public static JsonElement Payload(int bytes, int ordinal = 0)
    {
        JsonElement empty = JsonSerializer.SerializeToElement(new
        {
            ordinal,
            content = string.Empty
        });
        int overhead = Encoding.UTF8.GetByteCount(empty.GetRawText());
        if (bytes < overhead)
        {
            throw new ArgumentOutOfRangeException(
                nameof(bytes), $"Payload size must be at least {overhead} bytes for ordinal {ordinal}.");
        }

        JsonElement payload = JsonSerializer.SerializeToElement(new
        {
            ordinal,
            content = new string((char)('a' + (ordinal % 26)), bytes - overhead)
        });
        if (Encoding.UTF8.GetByteCount(payload.GetRawText()) != bytes)
        {
            throw new InvalidOperationException("The deterministic payload did not match its requested byte size.");
        }

        return payload;
    }

    public static CandidateEvent Candidate(int ordinal, int payloadBytes, int keyCount = 1, string type = "changed")
    {
        EventKey[] keys = Enumerable.Range(0, keyCount)
            .Select(index => new EventKey($"key-{index}", $"value-{ordinal:D8}-{index}"))
            .ToArray();
        return new CandidateEvent(type, Payload(payloadBytes, ordinal), keys);
    }

    public static SequencedEvent Sequenced(int ordinal, int payloadBytes = 128, int keyCount = 1,
        string type = "changed")
    {
        return new SequencedEvent(
            ordinal + 1L,
            type,
            Payload(payloadBytes, ordinal),
            Enumerable.Range(0, keyCount)
                .Select(index => new EventKey($"key-{index}", $"value-{ordinal:D8}-{index}"))
                .ToArray(),
            SchemaVersion: 1,
            Timestamp,
            GuidFor(ordinal + 1),
            "benchmark-command");
    }

    public static EventBatch Batch(int ordinal, int eventCount, int payloadBytes, int keyCount = 1)
    {
        return new EventBatch(
            GuidFor(ordinal + 1),
            "benchmark-command",
            Enumerable.Range(0, eventCount)
                .Select(index => Candidate((ordinal * eventCount) + index, payloadBytes, keyCount))
                .ToArray());
    }

    public static async Task SeedStoreAsync(
        string directory,
        int eventCount,
        int closedPartitions = 0,
        int payloadBytes = 128,
        int keyCount = 1,
        int batchesPerPartition = 1,
        Func<int, string>? eventType = null)
    {
        if (batchesPerPartition <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchesPerPartition));
        }

        int partitionSize = closedPartitions == 0
            ? Math.Max(eventCount + 1, 1)
            : Math.Max(1, (int)Math.Ceiling((double)eventCount / closedPartitions));
        DatabaseOptions options = new(directory) { MaxEventCountPerPartition = partitionSize };
        await using JsonEventStore store = await JsonEventStore.CreateAsync(options);
        int eventOrdinal = 0;
        int batchOrdinal = 0;
        int partitions = closedPartitions == 0 ? 1 : closedPartitions;
        for (int partition = 0; partition < partitions; partition++)
        {
            int partitionEventCount = closedPartitions == 0
                ? eventCount
                : Math.Min(partitionSize, eventCount - eventOrdinal);
            int partitionRemaining = partitionEventCount;
            for (int batch = 0; batch < batchesPerPartition && partitionRemaining > 0; batch++)
            {
                int remainingBatches = batchesPerPartition - batch;
                int count = (int)Math.Ceiling((double)partitionRemaining / remainingBatches);
                CandidateEvent[] candidates = Enumerable.Range(eventOrdinal, count)
                    .Select(index => Candidate(
                        index,
                        payloadBytes,
                        keyCount,
                        eventType?.Invoke(index) ?? "changed"))
                    .ToArray();
                EventBatch events = new(
                    GuidFor(batchOrdinal + 1),
                    "benchmark-command",
                    candidates);
                await store.AppendAsync(events, new AppendCondition(EventQuery.All, store.Head));
                eventOrdinal += count;
                partitionRemaining -= count;
                batchOrdinal++;
            }
        }
    }
}
