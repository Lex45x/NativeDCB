using System.Text.Json;

using Google.Protobuf;

using Grpc.Core;

using NativeDCB.Engine;
using NativeDCB.Engine.Actors;
using NativeDCB.Model;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Actors;
using NativeDCB.Server.Storage;

namespace NativeDCB.Server.Services;

public sealed class CommandGrpcService(DatabaseRegistry registry, IGrainFactory grains)
    : CommandService.CommandServiceBase
{
    public override async Task<ExecuteHandlerResponse> ExecuteHandler(
        ExecuteHandlerRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        Guid commandId = ParseCommandId(request.HasCommandId ? request.CommandId : null);
        EventListMessage existing = await ReadCommittedCommandAsync(
            database, commandId, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (existing.Events.Length > 0)
        {
            return AlreadyCommitted(commandId, existing.Events);
        }

        if (!database.WriteAvailable)
        {
            throw ProtocolMapper.Unavailable(
                $"Database '{database.Name}' is not accepting commands while in state '{database.Status}'.");
        }

        if (!database.Catalog.Handlers.TryGetValue(request.HandlerName, out HandlerCatalogEntry? handler))
        {
            throw ProtocolMapper.NotFound($"Handler '{request.HandlerName}' was not found.");
        }

        string commandJson;
        try
        {
            using JsonDocument document = JsonDocument.Parse(ProtocolMapper.Utf8(request.CommandJson));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return Failed(commandId, handler.CommandType, "InvalidCommand", "Command JSON must be an object.");
            }

            commandJson = document.RootElement.GetRawText();
        }
        catch (JsonException exception)
        {
            return Failed(commandId, handler.CommandType, "InvalidCommand", exception.Message);
        }

        try
        {
            ITransactionGrain transaction = grains.GetGrain<ITransactionGrain>($"{database.Name}|{commandId:D}");
            TransactionRequestMessage actorRequest = new(
                database.Name,
                commandId,
                handler.Name,
                handler.CommandType,
                handler.NdlSource,
                handler.SourceFingerprint,
                handler.PlanFingerprint,
                commandJson,
                database.Catalog.EventSchemas.Values
                    .Select(schema => new SchemaDocumentMessage(
                        schema.Name, schema.DocumentJson, schema.Fingerprint))
                    .ToArray(),
                database.Catalog.CommandSchemas.TryGetValue(
                    handler.CommandType, out SchemaCatalogEntry? commandSchema)
                    ? new SchemaDocumentMessage(
                        commandSchema.Name, commandSchema.DocumentJson, commandSchema.Fingerprint)
                    : null,
                handler.PlanJson);
            DecisionResultMessage result = await GrainCall.RunAsync(
                token => transaction.ExecuteAsync(actorRequest, token),
                context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            return ToResponse(result);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                              or DivideByZeroException)
        {
            return Failed(commandId, handler.CommandType, "InvalidCommand", exception.Message);
        }
    }

    public override async Task<GetEventsByCommandIdResponse> GetEventsByCommandId(
        GetEventsByCommandIdRequest request,
        ServerCallContext context)
    {
        DatabaseEntry database = GetDatabase(request.Database);
        if (!Guid.TryParseExact(request.CommandId, "D", out Guid commandId) || commandId == Guid.Empty)
        {
            throw ProtocolMapper.InvalidArgument("A canonical command UUID is required.");
        }

        EventListMessage result = await ReadCommittedCommandAsync(
            database, commandId, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (result.Events.Length == 0)
        {
            throw ProtocolMapper.NotFound($"Command '{request.CommandId}' was not found.");
        }

        GetEventsByCommandIdResponse response = new();
        response.Events.AddRange(result.Events.Select(ActorMessageMapper.ToModel).Select(ProtocolMapper.ToEnvelope));
        return response;
    }

    private static ExecuteHandlerResponse ToResponse(DecisionResultMessage result)
    {
        ExecuteHandlerResponse response = new()
        {
            CommandId = result.CommandId.ToString("D"), CommandType = result.CommandType
        };
        switch (result.Outcome)
        {
            case DecisionResultOutcome.Committed:
                response.Committed = new Committed
                {
                    FirstEventId = result.Events[0].EventId, LastEventId = result.Events[^1].EventId
                };
                response.Committed.Events.AddRange(
                    result.Events.Select(ActorMessageMapper.ToModel).Select(ProtocolMapper.ToEnvelope));
                break;
            case DecisionResultOutcome.AlreadyCommitted:
                response.AlreadyCommitted = new AlreadyCommitted
                {
                    FirstEventId = result.Events[0].EventId, LastEventId = result.Events[^1].EventId
                };
                break;
            case DecisionResultOutcome.Rejected:
                response.Rejected = new Rejected
                {
                    Code = result.Code, Message = result.Message, DetailsJson = ByteString.CopyFromUtf8("{}")
                };
                break;
            case DecisionResultOutcome.Failed:
                response.Failed = new CommandFailed
                {
                    Error = new ErrorDetail { Code = result.Code, Message = result.Message }
                };
                break;
            case DecisionResultOutcome.Unavailable:
                throw ProtocolMapper.Unavailable(result.Message ?? "The database writer is unavailable.");
            case DecisionResultOutcome.DataLoss:
                throw ProtocolMapper.DataLoss(result.Message ?? "The database contains invalid committed data.");
        }

        return response;
    }

    private static ExecuteHandlerResponse Failed(Guid commandId, string commandType, string code, string message)
    {
        return new ExecuteHandlerResponse
        {
            CommandId = commandId.ToString("D"),
            CommandType = commandType,
            Failed = new CommandFailed { Error = new ErrorDetail { Code = code, Message = message } }
        };
    }

    private static ExecuteHandlerResponse AlreadyCommitted(
        Guid commandId,
        SequencedEventMessage[] events)
    {
        return new ExecuteHandlerResponse
        {
            CommandId = commandId.ToString("D"),
            CommandType = events[0].CommandType,
            AlreadyCommitted = new AlreadyCommitted
            {
                FirstEventId = events[0].EventId, LastEventId = events[^1].EventId
            }
        };
    }

    private async Task<EventListMessage> ReadCommittedCommandAsync(
        DatabaseEntry database,
        Guid commandId,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!database.IsOpen)
            {
                long head = await PartitionEventReader.GetHeadAsync(database.Directory, cancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                IReadOnlyList<SequencedEvent> events =
                    await PartitionEventReader.ReadByCommandIdAsync(
                            database.Directory, commandId, head, cancellationToken)
                        .ConfigureAwait(continueOnCapturedContext: false);
                return new EventListMessage(events.Select(ActorMessageMapper.ToMessage).ToArray());
            }

            IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database.Name);
            WriterStateMessage state = await GrainCall.RunAsync(
                writer.GetStateAsync,
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
            IReadGrain reader = grains.GetGrain<IReadGrain>(database.Name);
            return await GrainCall.RunAsync(
                token => reader.ReadByCommandIdAsync(commandId, state.Head, token),
                cancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        }
        catch (InvalidDataException exception)
        {
            throw ProtocolMapper.DataLoss(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ProtocolMapper.Unavailable(exception.Message);
        }
    }

    private static Guid ParseCommandId(string? value)
    {
        if (value is null)
        {
            return Guid.NewGuid();
        }

        return Guid.TryParseExact(value, "D", out Guid commandId) && commandId != Guid.Empty
            ? commandId
            : throw ProtocolMapper.InvalidArgument("command_id must be a canonical non-empty UUID.");
    }

    private async Task<DatabaseEntry> GetDatabaseAsync(string name, CancellationToken cancellationToken)
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
}