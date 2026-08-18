using System.Security.Cryptography;
using System.Text.Json;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Engine.Actors.Contracts;
using NativeDCB.Engine.Actors.Mapping;
using NativeDCB.Engine.Actors.Messages;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;
using NativeDCB.Protocol.V1;
using NativeDCB.Server.Catalog;
using NativeDCB.Server.Catalog.Schemas;
using NativeDCB.Server.Databases;
using NativeDCB.Server.Decisions.Execution;
using NativeDCB.Server.Decisions.Remote;
using NativeDCB.Server.Decisions.Transactions;
using NativeDCB.Server.Grpc.Infrastructure;

namespace NativeDCB.Server.Grpc;

public sealed class CommandGrpcService(
    DatabaseRegistry registry,
    IGrainFactory grains,
    RemoteDecisionTokenProtector remoteDecisions)
    : CommandService.CommandServiceBase
{
    private readonly NdlDecisionRuntime _runtime = new();

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

    public override async Task<PrepareDecisionResponse> PrepareDecision(
        PrepareDecisionRequest request,
        ServerCallContext context)
    {
        EnsureRemoteDecisionsConfigured();
        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        Guid commandId = ParseCommandId(request.HasCommandId ? request.CommandId : null);
        EventListMessage existing = await ReadCommittedCommandAsync(
            database, commandId, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (existing.Events.Length > 0)
        {
            return PrepareAlreadyCommitted(commandId, existing.Events);
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

        database.Catalog.CommandSchemas.TryGetValue(
            handler.CommandType, out SchemaCatalogEntry? commandSchema);
        Dictionary<string, SchemaCatalogEntry> eventRegistrations = database.Catalog.EventSchemas.Values
            .ToDictionary(schema => schema.Name, StringComparer.Ordinal);

        byte[] commandBytes = ProtocolMapper.Utf8(request.CommandJson);
        using JsonDocument command = ParseCommand(commandBytes, commandId, handler.CommandType,
            out PrepareDecisionResponse? failure);
        if (failure is not null)
        {
            return failure;
        }

        try
        {
            if (commandSchema is not null)
            {
                RegisteredJsonSchema.Parse(
                        commandSchema.Name, commandSchema.DocumentJson, eventSchema: false)
                    .Validate(command.RootElement);
            }

            Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(eventRegistrations);
            JsonElement commandValue = command.RootElement.Clone();
            PreparedDecisionResult prepared = await GrainCall.RunAsync(
                    token => _runtime.PrepareAsync(
                        grains, database.Name, handler, eventSchemas, commandValue, token),
                    context.CancellationToken)
                .ConfigureAwait(continueOnCapturedContext: false);
            DateTimeOffset issuedUtc = remoteDecisions.UtcNow;
            DateTimeOffset expiresUtc = issuedUtc.Add(remoteDecisions.Lifetime);
            RemoteDecisionClaims claims = new(
                database.Name,
                database.Store.StoreId,
                commandId,
                handler.CommandType,
                handler.Name,
                handler.PlanFingerprint,
                Convert.ToHexString(SHA256.HashData(commandBytes)).ToLowerInvariant(),
                Convert.ToHexString(SHA256.HashData(prepared.ModelJson)).ToLowerInvariant(),
                prepared.ObservedHead,
                prepared.Query,
                RelevantSchemas(commandSchema, eventRegistrations, prepared.Plan),
                prepared.Plan.Emissions.Select(value => value.EventType).ToArray(),
                issuedUtc,
                expiresUtc,
                RandomNumberGenerator.GetBytes(count: 16));
            return new PrepareDecisionResponse
            {
                CommandId = commandId.ToString("D"),
                CommandType = handler.CommandType,
                Prepared = new PreparedDecision
                {
                    ModelJson = ByteString.CopyFrom(prepared.ModelJson),
                    ModelSignature = ByteString.CopyFrom(remoteDecisions.Protect(claims)),
                    ExpiresUtc = Timestamp.FromDateTimeOffset(expiresUtc),
                    PlanFingerprint = handler.PlanFingerprint
                }
            };
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (InvalidDataException exception)
        {
            throw ProtocolMapper.DataLoss(exception.Message);
        }
        catch (JsonException exception)
        {
            throw ProtocolMapper.DataLoss(exception.Message);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw ProtocolMapper.Unavailable(exception.Message);
        }
        catch (Exception exception) when (exception is InvalidOperationException or ArgumentException
                                              or DivideByZeroException)
        {
            return PrepareFailed(commandId, handler.CommandType, "InvalidCommand", exception.Message);
        }
    }

    public override async Task<CompleteDecisionResponse> CompleteDecision(
        CompleteDecisionRequest request,
        ServerCallContext context)
    {
        EnsureRemoteDecisionsConfigured();
        if (!remoteDecisions.TryUnprotect(request.ModelSignature.Span, out RemoteDecisionClaims? claims) ||
            claims is null)
        {
            throw ProtocolMapper.InvalidArgument("model_signature is invalid.");
        }

        DatabaseEntry database = await GetDatabaseAsync(request.Database, context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        if (!string.Equals(database.Name, claims.Database, StringComparison.Ordinal) ||
            database.Store.StoreId != claims.StoreId)
        {
            throw ProtocolMapper.InvalidArgument("model_signature is invalid.");
        }

        EventListMessage existing = await ReadCommittedCommandAsync(
            database, claims.CommandId, context.CancellationToken).ConfigureAwait(continueOnCapturedContext: false);
        if (existing.Events.Length > 0)
        {
            return CompleteAlreadyCommitted(claims.CommandId, existing.Events);
        }

        if (remoteDecisions.UtcNow >= claims.ExpiresUtc)
        {
            return new CompleteDecisionResponse
            {
                CommandId = claims.CommandId.ToString("D"),
                CommandType = claims.CommandType,
                Expired = new DecisionExpired { ExpiresUtc = Timestamp.FromDateTimeOffset(claims.ExpiresUtc) }
            };
        }

        if (request.ProposedEvents.Count != claims.ExpectedEventTypes.Length ||
            request.ProposedEvents.Where((proposed, index) =>
                    !string.Equals(proposed.Type, claims.ExpectedEventTypes[index], StringComparison.Ordinal))
                .Any())
        {
            return CompleteFailed(
                claims, "InvalidEvents", "Proposed events must match the decision plan's emission order and count.");
        }

        await database.CatalogLock.WaitAsync(context.CancellationToken)
            .ConfigureAwait(continueOnCapturedContext: false);
        try
        {
            if (!database.Catalog.Handlers.TryGetValue(claims.HandlerName, out HandlerCatalogEntry? handler) ||
                !string.Equals(handler.CommandType, claims.CommandType, StringComparison.Ordinal) ||
                !string.Equals(handler.PlanFingerprint, claims.PlanFingerprint, StringComparison.Ordinal))
            {
                return Invalidated(claims, "HandlerChanged", "The prepared decision handler has changed.");
            }

            foreach (RemoteSchemaFingerprint expected in claims.SchemaFingerprints)
            {
                SchemaCatalogEntry? current = expected.Kind switch
                {
                    "command" => database.Catalog.CommandSchemas.GetValueOrDefault(expected.Name),
                    "event" => database.Catalog.EventSchemas.GetValueOrDefault(expected.Name),
                    _ => null
                };
                if (expected.Kind is not ("command" or "event") ||
                    !string.Equals(current?.Fingerprint, expected.Fingerprint, StringComparison.Ordinal))
                {
                    return Invalidated(claims, "SchemaChanged", "A schema used by the prepared decision has changed.");
                }
            }

            if (!database.WriteAvailable)
            {
                throw ProtocolMapper.Unavailable(
                    $"Database '{database.Name}' is not accepting commands while in state '{database.Status}'.");
            }

            List<CandidateEvent> candidates = new(request.ProposedEvents.Count);
            Dictionary<string, RegisteredJsonSchema> parsedSchemas = new(StringComparer.Ordinal);
            try
            {
                foreach (ProposedEvent proposed in request.ProposedEvents)
                {
                    if (!database.Catalog.EventSchemas.TryGetValue(
                            proposed.Type, out SchemaCatalogEntry? registration))
                    {
                        return Invalidated(
                            claims, "EventSchemaMissing", $"Event schema '{proposed.Type}' is not registered.");
                    }

                    using JsonDocument payload = JsonDocument.Parse(ProtocolMapper.Utf8(proposed.DataJson));
                    if (payload.RootElement.ValueKind != JsonValueKind.Object)
                    {
                        throw new ArgumentException("Proposed event JSON must be an object.");
                    }

                    if (!parsedSchemas.TryGetValue(proposed.Type, out RegisteredJsonSchema? schema))
                    {
                        schema = RegisteredJsonSchema.Parse(
                            registration.Name, registration.DocumentJson, eventSchema: true);
                        parsedSchemas.Add(proposed.Type, schema);
                    }

                    candidates.Add(new CandidateEvent(
                        proposed.Type, payload.RootElement.Clone(), schema.ExtractKeys(payload.RootElement)));
                }
            }
            catch (JsonException exception)
            {
                return CompleteFailed(claims, "InvalidEvents", exception.Message);
            }
            catch (ArgumentException exception)
            {
                return CompleteFailed(claims, "InvalidEvents", exception.Message);
            }
            catch (Exception exception) when (exception is FormatException or OverflowException)
            {
                return CompleteFailed(claims, "InvalidEvents", exception.Message);
            }
            catch (InvalidDataException exception)
            {
                throw ProtocolMapper.DataLoss(exception.Message);
            }

            try
            {
                if (remoteDecisions.UtcNow >= claims.ExpiresUtc)
                {
                    return new CompleteDecisionResponse
                    {
                        CommandId = claims.CommandId.ToString("D"),
                        CommandType = claims.CommandType,
                        Expired = new DecisionExpired
                        {
                            ExpiresUtc = Timestamp.FromDateTimeOffset(claims.ExpiresUtc)
                        }
                    };
                }

                IMainWriterGrain writer = grains.GetGrain<IMainWriterGrain>(database.Name);
                AppendResultMessage append = await GrainCall.RunAsync(
                        token => writer.AppendAsync(
                            ActorMessageMapper.ToMessage(new EventBatch(
                                claims.CommandId, claims.CommandType, candidates)),
                            ActorMessageMapper.ToMessage(new AppendCondition(
                                claims.Query, claims.ObservedHead)),
                            token),
                        context.CancellationToken)
                    .ConfigureAwait(continueOnCapturedContext: false);
                return CompleteAppendResult(claims, append);
            }
            catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (InvalidDataException exception)
            {
                throw ProtocolMapper.DataLoss(exception.Message);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                                  or EventStoreUnavailableException)
            {
                throw ProtocolMapper.Unavailable(exception.Message);
            }
            catch (ArgumentException exception)
            {
                return CompleteFailed(claims, "InvalidEvents", exception.Message);
            }
        }
        finally
        {
            database.CatalogLock.Release();
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
            CommandId = result.CommandId.ToString("D"),
            CommandType = result.CommandType
        };
        switch (result.Outcome)
        {
            case DecisionResultOutcome.Committed:
                response.Committed = new Committed
                {
                    FirstEventId = result.Events[0].EventId,
                    LastEventId = result.Events[^1].EventId
                };
                response.Committed.Events.AddRange(
                    result.Events.Select(ActorMessageMapper.ToModel).Select(ProtocolMapper.ToEnvelope));
                break;
            case DecisionResultOutcome.AlreadyCommitted:
                response.AlreadyCommitted = new AlreadyCommitted
                {
                    FirstEventId = result.Events[0].EventId,
                    LastEventId = result.Events[^1].EventId
                };
                break;
            case DecisionResultOutcome.Rejected:
                response.Rejected = new Rejected
                {
                    Code = result.Code,
                    Message = result.Message,
                    DetailsJson = ByteString.CopyFromUtf8("{}")
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
                FirstEventId = events[0].EventId,
                LastEventId = events[^1].EventId
            }
        };
    }

    private static JsonDocument ParseCommand(
        byte[] commandBytes,
        Guid commandId,
        string commandType,
        out PrepareDecisionResponse? failure)
    {
        try
        {
            JsonDocument document = JsonDocument.Parse(commandBytes);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                failure = PrepareFailed(
                    commandId, commandType, "InvalidCommand", "Command JSON must be an object.");
            }
            else
            {
                failure = null;
            }

            return document;
        }
        catch (JsonException exception)
        {
            failure = PrepareFailed(commandId, commandType, "InvalidCommand", exception.Message);
            return JsonDocument.Parse("{}");
        }
    }

    private static Dictionary<string, RegisteredJsonSchema> ParseEventSchemas(
        IReadOnlyDictionary<string, SchemaCatalogEntry> registrations)
    {
        return registrations.Values.ToDictionary(
            schema => schema.Name,
            schema => RegisteredJsonSchema.Parse(schema.Name, schema.DocumentJson, eventSchema: true),
            StringComparer.Ordinal);
    }

    private static RemoteSchemaFingerprint[] RelevantSchemas(
        SchemaCatalogEntry? commandSchema,
        IReadOnlyDictionary<string, SchemaCatalogEntry> eventSchemas,
        DecisionPlan plan)
    {
        IEnumerable<RemoteSchemaFingerprint> command =
        [
            new(
                "command",
                plan.CommandSchema,
                commandSchema?.Fingerprint)
        ];
        IEnumerable<RemoteSchemaFingerprint> events = plan.Includes.Select(value => value.EventType)
            .Concat(plan.Emissions.Select(value => value.EventType))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name => new RemoteSchemaFingerprint(
                "event", name, eventSchemas.GetValueOrDefault(name)?.Fingerprint));
        return [.. command, .. events];
    }

    private static PrepareDecisionResponse PrepareAlreadyCommitted(
        Guid commandId,
        SequencedEventMessage[] events)
    {
        return new PrepareDecisionResponse
        {
            CommandId = commandId.ToString("D"),
            CommandType = events[0].CommandType,
            AlreadyCommitted = new AlreadyCommitted
            {
                FirstEventId = events[0].EventId,
                LastEventId = events[^1].EventId
            }
        };
    }

    private static PrepareDecisionResponse PrepareFailed(
        Guid commandId,
        string commandType,
        string code,
        string message)
    {
        return new PrepareDecisionResponse
        {
            CommandId = commandId.ToString("D"),
            CommandType = commandType,
            Failed = new CommandFailed { Error = new ErrorDetail { Code = code, Message = message } }
        };
    }

    private static CompleteDecisionResponse CompleteAlreadyCommitted(
        Guid commandId,
        SequencedEventMessage[] events)
    {
        return new CompleteDecisionResponse
        {
            CommandId = commandId.ToString("D"),
            CommandType = events[0].CommandType,
            AlreadyCommitted = new AlreadyCommitted
            {
                FirstEventId = events[0].EventId,
                LastEventId = events[^1].EventId
            }
        };
    }

    private static CompleteDecisionResponse Invalidated(
        RemoteDecisionClaims claims,
        string code,
        string message)
    {
        return new CompleteDecisionResponse
        {
            CommandId = claims.CommandId.ToString("D"),
            CommandType = claims.CommandType,
            Invalidated = new DecisionInvalidated { Code = code, Message = message }
        };
    }

    private static CompleteDecisionResponse CompleteFailed(
        RemoteDecisionClaims claims,
        string code,
        string message)
    {
        return new CompleteDecisionResponse
        {
            CommandId = claims.CommandId.ToString("D"),
            CommandType = claims.CommandType,
            Failed = new CommandFailed { Error = new ErrorDetail { Code = code, Message = message } }
        };
    }

    private static CompleteDecisionResponse CompleteAppendResult(
        RemoteDecisionClaims claims,
        AppendResultMessage append)
    {
        if (append.Outcome == AppendResultOutcome.Conflict)
        {
            return new CompleteDecisionResponse
            {
                CommandId = claims.CommandId.ToString("D"),
                CommandType = claims.CommandType,
                Stale = new DecisionStale { CurrentHead = append.Head }
            };
        }

        if (append.Outcome == AppendResultOutcome.AlreadyCommitted)
        {
            return CompleteAlreadyCommitted(claims.CommandId, append.Events);
        }

        CompleteDecisionResponse response = new()
        {
            CommandId = claims.CommandId.ToString("D"),
            CommandType = claims.CommandType,
            Committed = new Committed
            {
                FirstEventId = append.Events[0].EventId,
                LastEventId = append.Events[^1].EventId
            }
        };
        response.Committed.Events.AddRange(
            append.Events.Select(ActorMessageMapper.ToModel).Select(ProtocolMapper.ToEnvelope));
        return response;
    }

    private void EnsureRemoteDecisionsConfigured()
    {
        if (!remoteDecisions.IsConfigured)
        {
            throw ProtocolMapper.FailedPrecondition(
                remoteDecisions.ConfigurationError ?? "Remote decisions are not configured.");
        }
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