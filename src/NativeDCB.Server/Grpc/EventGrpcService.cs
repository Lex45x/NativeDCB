using Grpc.Core;

using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

using QueryItem = NativeDCB.Model.Queries.QueryItem;

namespace NativeDCB.Server.Grpc;

public sealed class EventGrpcService(IGrainFactory grains) : EventService.EventServiceBase
{
    private const int SubscriptionBatchSize = 256;

    public override async Task ReadEventsByRange(
        ReadEventsByRangeRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateDatabase(request.Database);
        ValidateRange(request.AfterEventId, request.HasThroughEventId ? request.ThroughEventId : null);
        ValidateLimit(request.HasLimit ? request.Limit : null);
        if (request.Mode is not (ReadMode.Unspecified or ReadMode.Snapshot or ReadMode.Follow))
        {
            throw ProtocolMapper.InvalidArgument("The read mode is invalid.");
        }

        if (request.Mode == ReadMode.Follow)
        {
            if (request.HasThroughEventId)
            {
                throw ProtocolMapper.InvalidArgument("Follow mode cannot specify through_event_id.");
            }

            EventSubscriptionOpenMessage open = new(
                request.Database,
                EventSubscriptionKind.Range,
                new EventQueryMessage([]),
                [],
                request.AfterEventId,
                request.HasLimit ? checked((int)request.Limit) : null);
            await StreamSubscriptionAsync(open, responseStream, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        IReadGrain reader = grains.GetGrain<IReadGrain>(request.Database);
        EventReadSnapshotMessage snapshot = await CallAsync(
                token => reader.ReadRangeSnapshotAsync(
                    request.AfterEventId,
                    request.HasThroughEventId ? request.ThroughEventId : null,
                    MaxCount(request.HasLimit, request.Limit),
                    token),
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        await WriteAsync(responseStream, snapshot.Events, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task ReadEventsByQuery(
        ReadEventsByQueryRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateDatabase(request.Database);
        ValidateRange(request.AfterEventId, request.HasThroughEventId ? request.ThroughEventId : null);
        ValidateLimit(request.HasLimit ? request.Limit : null);
        ValidateConsistency(request.Consistency);
        EventQueryMessage query = ActorMessageMapper.ToMessage(ProtocolMapper.ToQuery(request.Query));
        IndexQueryResultMessage snapshot = await ReadQuerySnapshotAsync(
                request.Database,
                query,
                request.AfterEventId,
                request.HasThroughEventId ? request.ThroughEventId : null,
                MaxCount(request.HasLimit, request.Limit),
                request.Consistency,
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        await WriteAsync(responseStream, snapshot.Events, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task ReadEventsByTypeAndKeys(
        ReadEventsByTypeAndKeysRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateDatabase(request.Database);
        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            throw ProtocolMapper.InvalidArgument("An event type is required.");
        }

        ValidateRange(request.AfterEventId, request.HasThroughEventId ? request.ThroughEventId : null);
        ValidateLimit(request.HasLimit ? request.Limit : null);
        ValidateConsistency(request.Consistency);
        EventQuery query = new([
            new QueryItem(
                [request.EventType], request.Keys.Select(ProtocolMapper.ToEventKey).ToArray())
        ]);
        IndexQueryResultMessage snapshot = await ReadQuerySnapshotAsync(
                request.Database,
                ActorMessageMapper.ToMessage(query),
                request.AfterEventId,
                request.HasThroughEventId ? request.ThroughEventId : null,
                MaxCount(request.HasLimit, request.Limit),
                request.Consistency,
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        await WriteAsync(responseStream, snapshot.Events, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task SubscribeEvents(
        SubscribeEventsRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateDatabase(request.Database);
        ValidateRange(request.AfterEventId, through: null);
        EventQueryMessage query = ActorMessageMapper.ToMessage(ProtocolMapper.ToQuery(request.Query));
        EventKeyMessage[] requiredKeys = request.Keys
            .Select(ProtocolMapper.ToEventKey)
            .Select(value => new EventKeyMessage(value.Name, value.Value))
            .ToArray();
        EventSubscriptionOpenMessage open = new(
            request.Database,
            EventSubscriptionKind.Query,
            query,
            requiredKeys,
            request.AfterEventId,
            Limit: null);
        await StreamSubscriptionAsync(open, responseStream, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task<IndexQueryResultMessage> ReadQuerySnapshotAsync(
        string database,
        EventQueryMessage query,
        long afterEventIdExclusive,
        long? throughEventIdInclusive,
        int maxCount,
        QueryConsistency consistency,
        CancellationToken cancellationToken)
    {
        IIndexOrchestratorGrain orchestrator = grains.GetGrain<IIndexOrchestratorGrain>(database);
        return consistency == QueryConsistency.EventualIndex
            ? await CallAsync(
                    token => orchestrator.ReadEventualAsync(
                        query, afterEventIdExclusive, throughEventIdInclusive, maxCount, token),
                    cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false)
            : await CallAsync(
                    token => orchestrator.ReadAuthoritativeAsync(
                        query, afterEventIdExclusive, throughEventIdInclusive, maxCount, token),
                    cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
    }

    private async Task StreamSubscriptionAsync(
        EventSubscriptionOpenMessage open,
        IServerStreamWriter<EventEnvelope> responseStream,
        CancellationToken cancellationToken)
    {
        IEventSubscriptionGrain subscription = grains.GetGrain<IEventSubscriptionGrain>(
            Guid.NewGuid().ToString("N"));
        try
        {
            await CallAsync(token => subscription.OpenAsync(open, token), cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            while (true)
            {
                EventSubscriptionReadResultMessage next = await CallAsync(
                        token => subscription.ReadNextAsync(
                            new EventSubscriptionReadNextMessage(SubscriptionBatchSize), token),
                        cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                await WriteAsync(responseStream, next.Events, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                if (next.Completed)
                {
                    return;
                }
            }
        }
        finally
        {
            await CallAsync(
                    token => subscription.CloseAsync(new EventSubscriptionCloseMessage(), token),
                    CancellationToken.None)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private static async Task WriteAsync(
        IServerStreamWriter<EventEnvelope> responseStream,
        IEnumerable<SequencedEventMessage> events,
        CancellationToken cancellationToken)
    {
        foreach (SequencedEventMessage value in events)
        {
            await responseStream.WriteAsync(
                    ProtocolMapper.ToEnvelope(ActorMessageMapper.ToModel(value)), cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
    }

    private static async Task<T> CallAsync<T>(
        Func<GrainCancellationToken, Task<T>> call,
        CancellationToken cancellationToken)
    {
        try
        {
            return await GrainCall.RunAsync(call, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (Exception exception) when (exception is not RpcException and not OperationCanceledException)
        {
            throw MapActorException(exception);
        }
    }

    private static async Task CallAsync(
        Func<GrainCancellationToken, Task> call,
        CancellationToken cancellationToken)
    {
        await CallAsync(async token =>
            {
                await call(token).ConfigureAwait(continueOnCapturedContext: false);
                return true;
            }, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    private static Exception MapActorException(Exception exception)
    {
        Exception root = exception.GetBaseException();
        return root switch
        {
            ArgumentException => ProtocolMapper.InvalidArgument(root.Message),
            DirectoryNotFoundException or KeyNotFoundException => ProtocolMapper.NotFound(root.Message),
            InvalidDataException => ProtocolMapper.DataLoss(root.Message),
            IOException or UnauthorizedAccessException => ProtocolMapper.Unavailable(root.Message),
            _ => exception
        };
    }

    private static int MaxCount(bool hasLimit, uint limit)
    {
        return hasLimit ? checked((int)limit) : int.MaxValue;
    }

    private static void ValidateDatabase(string database)
    {
        if (string.IsNullOrWhiteSpace(database) || Path.IsPathRooted(database))
        {
            throw ProtocolMapper.InvalidArgument("A relative database name is required.");
        }

        string normalized = database.Replace('\\', '/').Trim('/');
        if (normalized.Split('/').Any(segment => segment is "" or "." or ".."))
        {
            throw ProtocolMapper.InvalidArgument("The database name contains an invalid path segment.");
        }
    }

    private static void ValidateRange(long after, long? through)
    {
        if (after < 0 || through < 0 || (through is not null && through <= after))
        {
            throw ProtocolMapper.InvalidArgument("The event range is invalid.");
        }
    }

    private static void ValidateLimit(uint? limit)
    {
        if (limit is 0 or > int.MaxValue)
        {
            throw ProtocolMapper.InvalidArgument("limit must be between 1 and Int32.MaxValue when supplied.");
        }
    }

    private static void ValidateConsistency(QueryConsistency consistency)
    {
        if (consistency is not (QueryConsistency.Unspecified or
            QueryConsistency.EventualIndex or
            QueryConsistency.CommittedScan))
        {
            throw ProtocolMapper.InvalidArgument("The query consistency mode is invalid.");
        }
    }
}