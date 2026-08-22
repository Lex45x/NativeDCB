using BenchmarkDotNet.Attributes;

using NativeDCB.MicroBenchmarks.Infrastructure;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;

namespace NativeDCB.MicroBenchmarks.Queries;

[MemoryDiagnoser]
public class QueryMatchingBenchmarks
{
    public IEnumerable<QueryCase> EventQueryCases()
    {
        foreach (int items in new[] { 1, 4, 16 })
        foreach (int eventTypes in new[] { 1, 4 })
        foreach (int keys in new[] { 1, 4 })
        foreach (string position in new[] { "First", "Last", "Miss" })
        {
            SequencedEvent @event = BenchmarkFixtures.Sequenced(ordinal: 7, keyCount: keys, type: "target");
            int matchIndex = position switch
            {
                "First" => 0,
                "Last" => items - 1,
                _ => -1
            };
            QueryItem[] queryItems = Enumerable.Range(0, items)
                .Select(index => Item(index == matchIndex, eventTypes, keys))
                .ToArray();
            yield return new QueryCase(
                $"Items={items},Types={eventTypes},Keys={keys},Match={position}",
                new EventQuery(queryItems),
                @event);
        }
    }

    public IEnumerable<QueryItemCase> QueryItemCases()
    {
        foreach (int eventTypes in new[] { 1, 4, 16 })
        foreach (int keys in new[] { 1, 4 })
        foreach (bool matches in new[] { true, false })
        {
            yield return new QueryItemCase(
                $"Types={eventTypes},Keys={keys},Match={matches}",
                Item(matches, eventTypes, keys),
                BenchmarkFixtures.Sequenced(ordinal: 7, keyCount: keys, type: "target"));
        }
    }

    public IEnumerable<HistoryCase> HistoryCases()
    {
        foreach (int historySize in new[] { 64, 1000, 10_000 })
        foreach (string position in new[] { "First", "Last", "Miss" })
        {
            int matchIndex = position switch
            {
                "First" => 0,
                "Last" => historySize - 1,
                _ => -1
            };
            SequencedEvent[] history = Enumerable.Range(0, historySize)
                .Select(index => BenchmarkFixtures.Sequenced(
                    index,
                    type: index == matchIndex ? "target" : "other"))
                .ToArray();
            EventQuery query = new([new QueryItem(["other-0", "other-1", "other-2", "target"], [])]);
            yield return new HistoryCase($"History={historySize},Match={position}", query, history);
        }
    }

    [Benchmark]
    [ArgumentsSource(nameof(EventQueryCases))]
    public bool EventQueryMatches(QueryCase input) => input.Query.Matches(input.Event);

    [Benchmark]
    [ArgumentsSource(nameof(QueryItemCases))]
    public bool QueryItemMatches(QueryItemCase input) => input.Item.Matches(input.Event);

    [Benchmark]
    [ArgumentsSource(nameof(HistoryCases))]
    public int MatchHistory(HistoryCase input)
    {
        int matches = 0;
        foreach (SequencedEvent @event in input.History)
        {
            if (input.Query.Matches(@event))
            {
                matches++;
            }
        }

        return matches;
    }

    private static QueryItem Item(bool matches, int eventTypes, int keys)
    {
        string[] types = Enumerable.Range(0, eventTypes)
            .Select(index => matches && index == eventTypes - 1 ? "target" : $"other-{index}")
            .ToArray();
        EventKey[] required = Enumerable.Range(0, keys)
            .Select(index => new EventKey(
                $"key-{index}",
                matches ? $"value-00000007-{index}" : "missing"))
            .ToArray();
        return new QueryItem(types, required);
    }

    public sealed record QueryCase(string Shape, EventQuery Query, SequencedEvent Event)
    {
        public override string ToString() => Shape;
    }

    public sealed record QueryItemCase(string Shape, QueryItem Item, SequencedEvent Event)
    {
        public override string ToString() => Shape;
    }

    public sealed record HistoryCase(string Shape, EventQuery Query, IReadOnlyList<SequencedEvent> History)
    {
        public override string ToString() => Shape;
    }
}
