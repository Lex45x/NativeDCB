using Orleans.Concurrency;

// Orleans serialization contracts are consumed across process boundaries.
// ReSharper disable NotAccessedPositionalProperty.Global

namespace NativeDCB.Engine.Actors;

public interface IMainWriterGrain : IGrainWithStringKey
{
    Task<WriterStateMessage> GetStateAsync(GrainCancellationToken cancellationToken);

    Task<AppendResultMessage> AppendAsync(
        EventBatchMessage batch,
        AppendConditionMessage condition,
        GrainCancellationToken cancellationToken);
}

public interface IReadGrain : IGrainWithStringKey
{
    Task<PartitionListMessage> ListPartitionsAsync(
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadRangeAsync(
        long fromEventIdInclusive,
        long toEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadByQueryAsync(
        EventQueryMessage query,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadByCommandIdAsync(
        Guid commandId,
        long throughEventIdInclusive,
        GrainCancellationToken cancellationToken);
}

public interface ITransactionGrain : IGrainWithStringKey
{
    Task<DecisionResultMessage> ExecuteAsync(
        TransactionRequestMessage request,
        GrainCancellationToken cancellationToken);
}

public interface IStateBuilderGrain : IGrainWithStringKey
{
    [OneWay]
    Task BuildMissingAsync(int activePartition);

    [OneWay]
    Task RebuildAsync(int partitionNumber);
}

public interface IIndexCoordinatorGrain : IGrainWithStringKey
{
    [OneWay]
    Task ObserveAsync(EventListMessage events, long head);

    Task<IndexListMessage> ListAsync(long mainHead, GrainCancellationToken cancellationToken);

    Task<IndexReadResultMessage> ReadAsync(
        EventQueryMessage query,
        GrainCancellationToken cancellationToken);

    [OneWay]
    Task RebuildAsync(string eventType, EventKeyMessage[] keys, long mainHead);
}

public interface IIndexGrain : IGrainWithStringKey
{
    Task ObserveAsync(EventListMessage events, long head);

    Task RebuildAsync(long throughEventIdInclusive);

    Task<IndexStatusMessage> GetStatusAsync(long mainHead, GrainCancellationToken cancellationToken);

    Task<EventListMessage> ReadAsync(GrainCancellationToken cancellationToken);
}

[GenerateSerializer]
[Immutable]
public sealed record WriterStateMessage(
    [property: Id(id: 0)] long Head,
    [property: Id(id: 1)] int ActivePartition);

[GenerateSerializer]
[Immutable]
public sealed record EventKeyMessage(
    [property: Id(id: 0)] string Name,
    [property: Id(id: 1)] string Value);

[GenerateSerializer]
[Immutable]
public sealed record CandidateEventMessage(
    [property: Id(id: 0)] string Type,
    [property: Id(id: 1)] string DataJson,
    [property: Id(id: 2)] EventKeyMessage[] Keys,
    [property: Id(id: 3)] uint SchemaVersion);

[GenerateSerializer]
[Immutable]
public sealed record SequencedEventMessage(
    [property: Id(id: 0)] long EventId,
    [property: Id(id: 1)] string Type,
    [property: Id(id: 2)] string DataJson,
    [property: Id(id: 3)] EventKeyMessage[] Keys,
    [property: Id(id: 4)] uint SchemaVersion,
    [property: Id(id: 5)] DateTimeOffset TimestampUtc,
    [property: Id(id: 6)] Guid CommandId,
    [property: Id(id: 7)] string CommandType);

[GenerateSerializer]
[Immutable]
public sealed record EventBatchMessage(
    [property: Id(id: 0)] Guid CommandId,
    [property: Id(id: 1)] string CommandType,
    [property: Id(id: 2)] CandidateEventMessage[] Events);

[GenerateSerializer]
[Immutable]
public sealed record QueryItemMessage(
    [property: Id(id: 0)] string[] EventTypes,
    [property: Id(id: 1)] EventKeyMessage[] Keys);

[GenerateSerializer]
[Immutable]
public sealed record EventQueryMessage(
    [property: Id(id: 0)] QueryItemMessage[] Items);

[GenerateSerializer]
[Immutable]
public sealed record AppendConditionMessage(
    [property: Id(id: 0)] EventQueryMessage Query,
    [property: Id(id: 1)] long AfterEventId);

[GenerateSerializer]
[Immutable]
public sealed record AppendResultMessage(
    [property: Id(id: 0)] AppendResultOutcome Outcome,
    [property: Id(id: 1)] Guid CommandId,
    [property: Id(id: 2)] SequencedEventMessage[] Events,
    [property: Id(id: 3)] long Head);

[GenerateSerializer]
[Immutable]
public sealed record EventListMessage(
    [property: Id(id: 0)] SequencedEventMessage[] Events);

[GenerateSerializer]
[Immutable]
public sealed record PartitionStatusMessage(
    [property: Id(id: 0)] int PartitionNumber,
    [property: Id(id: 1)] string FilePath,
    [property: Id(id: 2)] bool IsActive,
    [property: Id(id: 3)] long? FirstEventId,
    [property: Id(id: 4)] long? LastEventId,
    [property: Id(id: 5)] int CommittedEventCount,
    [property: Id(id: 6)] long Length);

[GenerateSerializer]
[Immutable]
public sealed record PartitionListMessage(
    [property: Id(id: 0)] PartitionStatusMessage[] Partitions);

[GenerateSerializer]
[Immutable]
public sealed record IndexStatusMessage(
    [property: Id(id: 0)] string EventType,
    [property: Id(id: 1)] EventKeyMessage Key,
    [property: Id(id: 2)] string FilePath,
    [property: Id(id: 3)] long IndexHead,
    [property: Id(id: 4)] long MainHead,
    [property: Id(id: 5)] IndexHydrationState HydrationState,
    [property: Id(id: 6)] string? LastFault);

[GenerateSerializer]
[Immutable]
public sealed record IndexListMessage(
    [property: Id(id: 0)] IndexStatusMessage[] Indexes);

[GenerateSerializer]
[Immutable]
public sealed record IndexReadResultMessage(
    [property: Id(id: 0)] bool Supported,
    [property: Id(id: 1)] long IndexHead,
    [property: Id(id: 2)] SequencedEventMessage[] Events);

[GenerateSerializer]
[Immutable]
public sealed record TransactionRequestMessage(
    [property: Id(id: 0)] string Database,
    [property: Id(id: 1)] Guid CommandId,
    [property: Id(id: 2)] string HandlerName,
    [property: Id(id: 3)] string CommandType,
    [property: Id(id: 4)] string NdlSource,
    [property: Id(id: 5)] string SourceFingerprint,
    [property: Id(id: 6)] string PlanFingerprint,
    [property: Id(id: 7)] string CommandJson,
    [property: Id(id: 8)] SchemaDocumentMessage[] EventSchemas,
    [property: Id(id: 9)] SchemaDocumentMessage? CommandSchema,
    [property: Id(id: 10)] string PlanJson);

[GenerateSerializer]
[Immutable]
public sealed record SchemaDocumentMessage(
    [property: Id(id: 0)] string Name,
    [property: Id(id: 1)] string DocumentJson,
    [property: Id(id: 2)] string Fingerprint);

[GenerateSerializer]
[Immutable]
public sealed record DecisionResultMessage(
    [property: Id(id: 0)] DecisionResultOutcome Outcome,
    [property: Id(id: 1)] Guid CommandId,
    [property: Id(id: 2)] string CommandType,
    [property: Id(id: 3)] SequencedEventMessage[] Events,
    [property: Id(id: 4)] string? Code,
    [property: Id(id: 5)] string? Message);

public enum AppendResultOutcome
{
    Committed,
    Conflict,
    AlreadyCommitted
}

public enum DecisionResultOutcome
{
    Committed,
    AlreadyCommitted,
    Rejected,
    Failed,
    Unavailable,
    DataLoss
}

public enum IndexHydrationState
{
    Pending,
    Hydrating,
    Ready,
    Faulted
}