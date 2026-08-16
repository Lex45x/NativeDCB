using System.Text.Json;

using NativeDCB.Model;

namespace NativeDCB.Engine.Actors;

public static class ActorMessageMapper
{
    public static EventQueryMessage ToMessage(EventQuery query)
    {
        return new EventQueryMessage(query.Items.Select(item => new QueryItemMessage(
            item.EventTypes.ToArray(),
            item.Keys.Select(ToMessage).ToArray())).ToArray());
    }

    public static EventQuery ToModel(EventQueryMessage query)
    {
        return new EventQuery(query.Items.Select(item => new QueryItem(
            item.EventTypes,
            item.Keys.Select(ToModel).ToArray())).ToArray());
    }

    public static EventBatchMessage ToMessage(EventBatch batch)
    {
        return new EventBatchMessage(
            batch.CommandId,
            batch.CommandType,
            batch.Events.Select(ToMessage).ToArray());
    }

    public static EventBatch ToModel(EventBatchMessage batch)
    {
        return new EventBatch(
            batch.CommandId,
            batch.CommandType,
            batch.Events.Select(ToModel).ToArray());
    }

    public static AppendConditionMessage ToMessage(AppendCondition condition)
    {
        return new AppendConditionMessage(
            ToMessage(condition.Query),
            condition.AfterEventId);
    }

    public static AppendCondition ToModel(AppendConditionMessage condition)
    {
        return new AppendCondition(
            ToModel(condition.Query),
            condition.AfterEventId);
    }

    public static SequencedEventMessage ToMessage(SequencedEvent value)
    {
        return new SequencedEventMessage(
            value.EventId,
            value.Type,
            value.Data.GetRawText(),
            value.Keys.Select(ToMessage).ToArray(),
            value.SchemaVersion,
            value.TimestampUtc,
            value.CommandId,
            value.CommandType);
    }

    public static SequencedEvent ToModel(SequencedEventMessage value)
    {
        return new SequencedEvent(
            value.EventId,
            value.Type,
            ParseJson(value.DataJson),
            value.Keys.Select(ToModel).ToArray(),
            value.SchemaVersion,
            value.TimestampUtc,
            value.CommandId,
            value.CommandType);
    }

    public static AppendResultMessage ToMessage(AppendResult result)
    {
        return new AppendResultMessage(
            result.Outcome switch
            {
                AppendOutcome.Committed => AppendResultOutcome.Committed,
                AppendOutcome.Conflict => AppendResultOutcome.Conflict,
                AppendOutcome.AlreadyCommitted => AppendResultOutcome.AlreadyCommitted,
                _ => throw new ArgumentOutOfRangeException(nameof(result))
            },
            result.CommandId,
            result.Events.Select(ToMessage).ToArray(),
            result.Head);
    }

    private static CandidateEventMessage ToMessage(CandidateEvent value)
    {
        return new CandidateEventMessage(
            value.Type,
            value.Data.GetRawText(),
            value.Keys.Select(ToMessage).ToArray(),
            value.SchemaVersion);
    }

    private static CandidateEvent ToModel(CandidateEventMessage value)
    {
        return new CandidateEvent(
            value.Type,
            ParseJson(value.DataJson),
            value.Keys.Select(ToModel).ToArray(),
            value.SchemaVersion);
    }

    private static EventKeyMessage ToMessage(EventKey value)
    {
        return new EventKeyMessage(value.Name, value.Value);
    }

    private static EventKey ToModel(EventKeyMessage value)
    {
        return new EventKey(value.Name, value.Value);
    }

    private static JsonElement ParseJson(string json)
    {
        using JsonDocument document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}