using System.Text;

using BenchmarkDotNet.Attributes;

using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.MicroBenchmarks.Infrastructure;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.MicroBenchmarks.Mapping;

[MemoryDiagnoser]
public class ActorMappingBenchmarks
{
    public IEnumerable<object[]> BatchCases()
    {
        foreach (int eventCount in new[] { 1, 16 })
        foreach (int payloadBytes in new[] { 128, 1024 })
        foreach (int keys in new[] { 1, 4 })
        {
            EventBatch batch = BenchmarkFixtures.Batch(ordinal: 0, eventCount, payloadBytes, keys);
            int actualPayloadBytes = Encoding.UTF8.GetByteCount(batch.Events[0].Data.GetRawText());
            yield return
            [
                new BatchCase(
                    $"Events={eventCount},Keys={keys}",
                    batch,
                    ActorMessageMapper.ToMessage(batch)),
                actualPayloadBytes
            ];
        }
    }

    public IEnumerable<QueryCase> QueryCases()
    {
        yield return Query("Single", items: 1, eventTypes: 1, keys: 1);
        yield return Query("ItemUnion", items: 4, eventTypes: 1, keys: 1);
        yield return Query("TypeUnion", items: 1, eventTypes: 4, keys: 1);
        yield return Query("KeyIntersection", items: 1, eventTypes: 1, keys: 4);
        yield return Query("Mixed", items: 4, eventTypes: 4, keys: 4);
    }

    [Benchmark]
    [ArgumentsSource(nameof(BatchCases))]
    public EventBatchMessage ModelBatchToMessage(BatchCase input, int payloadBytes)
    {
        _ = payloadBytes;
        return ActorMessageMapper.ToMessage(input.Model);
    }

    [Benchmark]
    [ArgumentsSource(nameof(BatchCases))]
    public EventBatch MessageBatchToModel(BatchCase input, int payloadBytes)
    {
        _ = payloadBytes;
        return ActorMessageMapper.ToModel(input.Message);
    }

    [Benchmark]
    [ArgumentsSource(nameof(QueryCases))]
    public EventQueryMessage ModelQueryToMessage(QueryCase input) => ActorMessageMapper.ToMessage(input.Model);

    [Benchmark]
    [ArgumentsSource(nameof(QueryCases))]
    public EventQuery MessageQueryToModel(QueryCase input) => ActorMessageMapper.ToModel(input.Message);

    private static QueryCase Query(string name, int items, int eventTypes, int keys)
    {
        EventQuery query = new(Enumerable.Range(0, items)
            .Select(item => new QueryItem(
                Enumerable.Range(0, eventTypes).Select(type => $"type-{item}-{type}").ToArray(),
                Enumerable.Range(0, keys)
                    .Select(key => new EventKey($"key-{key}", $"value-{item}-{key}"))
                    .ToArray()))
            .ToArray());
        return new QueryCase(
            $"Shape={name},Items={items},Types={eventTypes},Keys={keys}",
            query,
            ActorMessageMapper.ToMessage(query));
    }

    public sealed record BatchCase(string Shape, EventBatch Model, EventBatchMessage Message)
    {
        public override string ToString() => Shape;
    }

    public sealed record QueryCase(string Shape, EventQuery Model, EventQueryMessage Message)
    {
        public override string ToString() => Shape;
    }
}
