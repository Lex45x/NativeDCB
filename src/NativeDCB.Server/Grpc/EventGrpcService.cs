using System.Threading.Channels;

using Grpc.Core;

using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Actors.Mapping;
using NativeDCB.Engine.Actors.Messages;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Databases;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

using QueryItem = NativeDCB.Model.Queries.QueryItem;

namespace NativeDCB.Server.Grpc;

public sealed class EventGrpcService(DatabaseRegistry registry, IGrainFactory grains) : EventService.EventServiceBase
{
    public override async Task ReadEventsByRange(
        ReadEventsByRangeRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateRange(request.AfterEventId, request.HasThroughEventId ? request.ThroughEventId : null);
        ValidateLimit(request.HasLimit ? request.Limit : null);
        if (request.Mode is not (ReadMode.Unspecified or ReadMode.Snapshot or ReadMode.Follow))
        {
            throw ProtocolMapper.InvalidArgument("The read mode is invalid.");
        }

        DatabaseEntry database = GetDatabase(request.Database);
        if (request.Mode == ReadMode.Follow)
        {
            if (request.HasThroughEventId)
            {
                throw ProtocolMapper.InvalidArgument("Follow mode cannot specify through_event_id.");
            }

            DatabaseEntry openDatabase = await OpenDatabaseAsync(request.Database, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            await FollowRangeAsync(
                    openDatabase, request.AfterEventId, responseStream,
                    request.HasLimit ? request.Limit : null, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            return;
        }

        long head = await GetHeadAsync(database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        long through = request.HasThroughEventId ? Math.Min(request.ThroughEventId, head) : head;
        IReadOnlyList<SequencedEvent> events = await ReadRangeAsync(
                database, request.AfterEventId, through, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        await ProtocolMapper.WriteAsync(responseStream, events, request.HasLimit ? request.Limit : null,
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task ReadEventsByQuery(
        ReadEventsByQueryRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateRange(request.AfterEventId, request.HasThroughEventId ? request.ThroughEventId : null);
        ValidateLimit(request.HasLimit ? request.Limit : null);
        ValidateConsistency(request.Consistency);
        DatabaseEntry database = GetDatabase(request.Database);
        long head = await GetHeadAsync(database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        long through = request.HasThroughEventId ? Math.Min(request.ThroughEventId, head) : head;
        EventQuery query = ProtocolMapper.ToQuery(request.Query);
        IReadOnlyList<SequencedEvent> events = await ReadByQueryAsync(
                database, query, through, request.Consistency, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        IEnumerable<SequencedEvent> selected =
            events.Where(x => x.EventId > request.AfterEventId && x.EventId <= through);
        await ProtocolMapper.WriteAsync(responseStream, selected, request.HasLimit ? request.Limit : null,
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task ReadEventsByTypeAndKeys(
        ReadEventsByTypeAndKeysRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        if (string.IsNullOrWhiteSpace(request.EventType))
        {
            throw ProtocolMapper.InvalidArgument("An event type is required.");
        }

        ValidateRange(request.AfterEventId, request.HasThroughEventId ? request.ThroughEventId : null);
        ValidateLimit(request.HasLimit ? request.Limit : null);
        ValidateConsistency(request.Consistency);
        DatabaseEntry database = GetDatabase(request.Database);
        long head = await GetHeadAsync(database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        long through = request.HasThroughEventId ? Math.Min(request.ThroughEventId, head) : head;
        EventQuery query = new([
            new QueryItem(
                [request.EventType], request.Keys.Select(ProtocolMapper.ToEventKey).ToArray())
        ]);
        IReadOnlyList<SequencedEvent> events = await ReadByQueryAsync(
                database, query, through, request.Consistency, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        IEnumerable<SequencedEvent> selected =
            events.Where(x => x.EventId > request.AfterEventId && x.EventId <= through);
        await ProtocolMapper.WriteAsync(responseStream, selected, request.HasLimit ? request.Limit : null,
                context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
    }

    public override async Task SubscribeEvents(
        SubscribeEventsRequest request,
        IServerStreamWriter<EventEnvelope> responseStream,
        ServerCallContext context)
    {
        ValidateRange(request.AfterEventId, through: null);
        DatabaseEntry database = await OpenDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        EventQuery query = ProtocolMapper.ToQuery(request.Query);
        EventKey[] simpleKeys = request.Keys.Select(ProtocolMapper.ToEventKey).ToArray();
        Channel<SequencedEvent> channel = Channel.CreateBounded<SequencedEvent>(
            new BoundedChannelOptions(capacity: 1024)
            {
                SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait
            });
        long lastWritten = request.AfterEventId;
        int overflowed = 0;

        void OnCommitted(SequencedEvent value)
        {
            // ReSharper disable once AccessToModifiedClosure -- Volatile coordinates replay and live callbacks.
            if (value.EventId > Volatile.Read(ref lastWritten) && Matches(value, query, simpleKeys))
            {
                if (!channel.Writer.TryWrite(value))
                {
                    // ReSharper disable once AccessToModifiedClosure -- Interlocked publishes overflow to the reader.
                    Interlocked.Exchange(ref overflowed, value: 1);
                    channel.Writer.TryComplete();
                }
            }
        }

        database.Store.EventCommitted += OnCommitted;
        try
        {
            long replayHead = await GetHeadAsync(database, context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            IReadOnlyList<SequencedEvent> replay = await ReadByQueryAsync(
                database, query, replayHead, QueryConsistency.CommittedScan,
                context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            foreach (SequencedEvent value in replay.Where(x =>
                         x.EventId > request.AfterEventId && x.EventId <= replayHead && Matches(x, query, simpleKeys)))
            {
                await responseStream.WriteAsync(ProtocolMapper.ToEnvelope(value), context.CancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                Volatile.Write(ref lastWritten, value.EventId);
            }

            await foreach (SequencedEvent value in channel.Reader.ReadAllAsync(context.CancellationToken)
                               .ConfigureAwait(continueOnCapturedContext: false))
            {
                if (value.EventId <= Volatile.Read(ref lastWritten))
                {
                    continue;
                }

                await responseStream.WriteAsync(ProtocolMapper.ToEnvelope(value), context.CancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                Volatile.Write(ref lastWritten, value.EventId);
            }

            if (Volatile.Read(ref overflowed) != 0)
            {
                throw ProtocolMapper.ResourceExhausted(
                    "The subscription consumer exceeded the live-event queue capacity.");
            }
        }
        finally
        {
            database.Store.EventCommitted -= OnCommitted;
            channel.Writer.TryComplete();
        }
    }

    private static bool Matches(SequencedEvent value, EventQuery query, IReadOnlyList<EventKey> simpleKeys)
    {
        return query.Matches(value) && simpleKeys.All(value.Keys.Contains);
    }

    private async Task FollowRangeAsync(
        DatabaseEntry database,
        long after,
        IServerStreamWriter<EventEnvelope> responseStream,
        uint? limit,
        CancellationToken cancellationToken)
    {
        Channel<SequencedEvent> channel = Channel.CreateBounded<SequencedEvent>(
            new BoundedChannelOptions(capacity: 1024)
            {
                SingleWriter = false, SingleReader = true, FullMode = BoundedChannelFullMode.Wait
            });
        long lastWritten = after;
        int written = 0;
        int overflowed = 0;

        void OnCommitted(SequencedEvent value)
        {
            // ReSharper disable once AccessToModifiedClosure -- Volatile coordinates replay and live callbacks.
            if (value.EventId > Volatile.Read(ref lastWritten) && !channel.Writer.TryWrite(value))
            {
                // ReSharper disable once AccessToModifiedClosure -- Interlocked publishes overflow to the reader.
                Interlocked.Exchange(ref overflowed, value: 1);
                channel.Writer.TryComplete();
            }
        }

        database.Store.EventCommitted += OnCommitted;
        try
        {
            long replayHead = await GetHeadAsync(database, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            IReadOnlyList<SequencedEvent> replay = await ReadRangeAsync(
                database, after, replayHead, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            foreach (SequencedEvent value in replay)
            {
                await responseStream.WriteAsync(ProtocolMapper.ToEnvelope(value), cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                Volatile.Write(ref lastWritten, value.EventId);
                written++;
                if (limit is not null && written >= limit)
                {
                    return;
                }
            }

            await foreach (SequencedEvent value in channel.Reader.ReadAllAsync(cancellationToken)
                               .ConfigureAwait(continueOnCapturedContext: false))
            {
                if (value.EventId <= Volatile.Read(ref lastWritten))
                {
                    continue;
                }

                await responseStream.WriteAsync(ProtocolMapper.ToEnvelope(value), cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                Volatile.Write(ref lastWritten, value.EventId);
                written++;
                if (limit is not null && written >= limit)
                {
                    return;
                }
            }

            if (Volatile.Read(ref overflowed) != 0)
            {
                throw ProtocolMapper.ResourceExhausted("The follow consumer exceeded the live-event queue capacity.");
            }
        }
        finally
        {
            database.Store.EventCommitted -= OnCommitted;
            channel.Writer.TryComplete();
        }
    }

    private async Task<IReadOnlyList<SequencedEvent>> ReadRangeAsync(
        DatabaseEntry database,
        long after,
        long through,
        CancellationToken cancellationToken)
    {
        if (through <= after || through == 0)
        {
            return [];
        }

        IReadGrain reader = grains.GetGrain<IReadGrain>(database.Name);
        EventListMessage result = await GrainCall.RunAsync(
            token => reader.ReadRangeAsync(after + 1, through, token),
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return result.Events.Select(ActorMessageMapper.ToModel).ToArray();
    }

    private async Task<IReadOnlyList<SequencedEvent>> ReadByQueryAsync(
        DatabaseEntry database,
        EventQuery query,
        long through,
        QueryConsistency consistency,
        CancellationToken cancellationToken)
    {
        if (consistency == QueryConsistency.EventualIndex && database.IsOpen)
        {
            IIndexCoordinatorGrain coordinator = grains.GetGrain<IIndexCoordinatorGrain>(database.Name);
            IndexReadResultMessage indexed = await GrainCall.RunAsync(
                token => coordinator.ReadAsync(ActorMessageMapper.ToMessage(query), token),
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            if (indexed.Supported)
            {
                return indexed.Events
                    .Where(value => value.EventId <= through)
                    .Select(ActorMessageMapper.ToModel)
                    .ToArray();
            }
        }

        IReadGrain reader = grains.GetGrain<IReadGrain>(database.Name);
        EventListMessage result = await GrainCall.RunAsync(
            token => reader.ReadByQueryAsync(ActorMessageMapper.ToMessage(query), through, token),
            cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        return result.Events.Select(ActorMessageMapper.ToModel).ToArray();
    }

    private async Task<long> GetHeadAsync(DatabaseEntry database, CancellationToken cancellationToken)
    {
        if (!database.IsOpen)
        {
            return await PartitionEventReader.GetHeadAsync(database.Directory, cancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
        }

        IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database.Name);
        WriterStateMessage state = await GrainCall.RunAsync(writer.GetStateAsync, cancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        return state.Head;
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

    private DatabaseEntry GetDatabase(string name)
    {
        try
        {
            return registry.Get(name);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            throw ProtocolMapper.NotFound(exception.Message);
        }
    }

    private async Task<DatabaseEntry> OpenDatabaseAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            return await registry.GetAsync(name, cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (ArgumentException exception)
        {
            throw ProtocolMapper.InvalidArgument(exception.Message);
        }
        catch (KeyNotFoundException exception)
        {
            throw ProtocolMapper.NotFound(exception.Message);
        }
    }
}