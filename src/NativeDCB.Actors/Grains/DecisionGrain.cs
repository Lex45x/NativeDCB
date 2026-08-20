using System.Security.Cryptography;
using System.Text.Json;

using NativeDCB.Actors.Catalog;
using NativeDCB.Actors.Catalog.Schemas;
using NativeDCB.Actors.Contracts;
using NativeDCB.Actors.Decisions.Execution;
using NativeDCB.Actors.Decisions.Remote;
using NativeDCB.Actors.Mapping;
using NativeDCB.Actors.Messages;
using NativeDCB.Actors.Storage;
using NativeDCB.Engine.Storage.EventLog;
using NativeDCB.Model.Decisions;
using NativeDCB.Model.Events;
using NativeDCB.Model.Events.Appending;

namespace NativeDCB.Actors.Grains;

// Orleans activates one decision grain per database and command ID.
// ReSharper disable once UnusedType.Global
public sealed class DecisionGrain(
    IGrainFactory grains,
    RemoteDecisionTokenProtector remoteDecisions) : Grain, IDecisionGrain
{
    private readonly NdlDecisionRuntime _runtime = new();

    public async Task<ExecuteDecisionResultMessage> ExecuteAsync(
        ExecuteDecisionMessage request,
        GrainCancellationToken cancellationToken)
    {
        try
        {
            _ = ActorStoragePath.NormalizeDatabaseName(request.Database);
        }
        catch (ArgumentException exception)
        {
            return ExecuteError(request, DecisionCallErrorKind.InvalidArgument,
                "InvalidArgument", exception.Message);
        }

        if (!ValidKey(request.Database, request.CommandId))
        {
            return ExecuteError(request, DecisionCallErrorKind.InvalidArgument,
                "CommandKeyMismatch", "The decision actor key does not match the request.");
        }

        try
        {
            CommandReadSnapshotMessage existing = await Reader(request.Database).ReadCommandSnapshotAsync(
                    request.CommandId, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (existing.Events.Length > 0)
            {
                return new ExecuteDecisionResultMessage(
                    ExecuteDecisionOutcome.AlreadyCommitted,
                    request.CommandId,
                    existing.Events[0].CommandType,
                    existing.Events);
            }

            HandlerDescriptionMessage? handler = await GetHandlerAsync(
                    request.Database, request.HandlerName, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (handler is null)
            {
                return ExecuteError(request, DecisionCallErrorKind.NotFound,
                    "NotFound", $"Handler '{request.HandlerName}' was not found.");
            }

            SchemaSnapshotMessage snapshot = await Schemas(request.Database).GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            using JsonDocument command = ParseObject(request.CommandJson, "Command JSON must be an object.");
            ValidateCommand(handler.CommandType, command.RootElement, snapshot);
            Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(snapshot);
            Dictionary<string, uint> eventSchemaVersions = snapshot.EventSchemas.ToDictionary(
                value => value.Name, value => value.Version, StringComparer.Ordinal);
            DecisionExecution execution = await _runtime.ExecuteAsync(
                    grains,
                    request.Database,
                    ToCatalogEntry(handler),
                    eventSchemas,
                    eventSchemaVersions,
                    request.CommandId,
                    command.RootElement,
                    cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return new ExecuteDecisionResultMessage(
                execution.Outcome switch
                {
                    DecisionOutcome.Committed => ExecuteDecisionOutcome.Committed,
                    DecisionOutcome.AlreadyCommitted => ExecuteDecisionOutcome.AlreadyCommitted,
                    DecisionOutcome.Rejected => ExecuteDecisionOutcome.Rejected,
                    _ => throw new ArgumentOutOfRangeException(nameof(execution))
                },
                request.CommandId,
                handler.CommandType,
                execution.Events.Select(ActorMessageMapper.ToMessage).ToArray(),
                execution.RejectionCode,
                execution.RejectionMessage);
        }
        catch (OperationCanceledException) when (cancellationToken.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return ExecuteException(request, exception);
        }
    }

    public async Task<PrepareDecisionResultMessage> PrepareAsync(
        PrepareDecisionMessage request,
        GrainCancellationToken cancellationToken)
    {
        try
        {
            _ = ActorStoragePath.NormalizeDatabaseName(request.Database);
        }
        catch (ArgumentException exception)
        {
            return PrepareError(request, DecisionCallErrorKind.InvalidArgument,
                "InvalidArgument", exception.Message);
        }

        if (!ValidKey(request.Database, request.CommandId))
        {
            return PrepareError(request, DecisionCallErrorKind.InvalidArgument,
                "CommandKeyMismatch", "The decision actor key does not match the request.");
        }

        if (!remoteDecisions.IsConfigured)
        {
            return PrepareError(request, DecisionCallErrorKind.FailedPrecondition,
                "RemoteDecisionsNotConfigured",
                remoteDecisions.ConfigurationError ?? "Remote decisions are not configured.");
        }

        try
        {
            CommandReadSnapshotMessage existing = await Reader(request.Database).ReadCommandSnapshotAsync(
                    request.CommandId, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (existing.Events.Length > 0)
            {
                return new PrepareDecisionResultMessage(
                    PrepareDecisionOutcome.AlreadyCommitted,
                    request.CommandId,
                    existing.Events[0].CommandType,
                    existing.Events,
                    [],
                    [],
                    default,
                    string.Empty);
            }

            HandlerDescriptionMessage? handler = await GetHandlerAsync(
                    request.Database, request.HandlerName, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (handler is null)
            {
                return PrepareError(request, DecisionCallErrorKind.NotFound,
                    "NotFound", $"Handler '{request.HandlerName}' was not found.");
            }

            SchemaSnapshotMessage snapshot = await Schemas(request.Database).GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            using JsonDocument command = ParseObject(request.CommandJson, "Command JSON must be an object.");
            ValidateCommand(handler.CommandType, command.RootElement, snapshot);
            Dictionary<string, RegisteredJsonSchema> eventSchemas = ParseEventSchemas(snapshot);
            HandlerCatalogEntry catalogHandler = ToCatalogEntry(handler);
            PreparedDecisionResult prepared = await _runtime.PrepareAsync(
                    grains,
                    request.Database,
                    catalogHandler,
                    eventSchemas,
                    command.RootElement,
                    cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            DateTimeOffset issuedUtc = remoteDecisions.UtcNow;
            DateTimeOffset expiresUtc = issuedUtc.Add(remoteDecisions.Lifetime);
            RemoteDecisionClaimsMessage claims = new(
                request.Database,
                existing.StoreId,
                request.CommandId,
                handler.CommandType,
                handler.Name,
                handler.PlanFingerprint,
                handler.Version,
                Hash(request.CommandJson),
                Hash(prepared.ModelJson),
                prepared.ObservedHead,
                ActorMessageMapper.ToMessage(prepared.Query),
                RelevantSchemas(snapshot, prepared.Plan),
                prepared.Plan.Emissions.Select(value => value.EventType).ToArray(),
                issuedUtc,
                expiresUtc,
                RandomNumberGenerator.GetBytes(16));
            return new PrepareDecisionResultMessage(
                PrepareDecisionOutcome.Prepared,
                request.CommandId,
                handler.CommandType,
                [],
                prepared.ModelJson,
                remoteDecisions.Protect(claims),
                expiresUtc,
                handler.PlanFingerprint);
        }
        catch (OperationCanceledException) when (cancellationToken.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return PrepareException(request, exception);
        }
    }

    public async Task<CompleteDecisionResultMessage> CompleteAsync(
        CompleteDecisionMessage request,
        GrainCancellationToken cancellationToken)
    {
        RemoteDecisionClaimsMessage claims = request.Claims;
        if (!ValidKey(claims.Database, claims.CommandId))
        {
            return CompleteError(claims, DecisionCallErrorKind.InvalidArgument,
                "InvalidSignature", "model_signature is invalid.");
        }

        try
        {
            CommandReadSnapshotMessage existing = await Reader(claims.Database).ReadCommandSnapshotAsync(
                    claims.CommandId, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (existing.StoreId != claims.StoreId)
            {
                return CompleteError(claims, DecisionCallErrorKind.InvalidArgument,
                    "InvalidSignature", "model_signature is invalid.");
            }

            if (existing.Events.Length > 0)
            {
                return new CompleteDecisionResultMessage(
                    CompleteDecisionOutcome.AlreadyCommitted,
                    claims.CommandId,
                    existing.Events[0].CommandType,
                    existing.Events);
            }

            if (remoteDecisions.UtcNow >= claims.ExpiresUtc)
            {
                return new CompleteDecisionResultMessage(
                    CompleteDecisionOutcome.Expired,
                    claims.CommandId,
                    claims.CommandType,
                    [],
                    ExpiresUtc: claims.ExpiresUtc);
            }

            if (request.ProposedEvents.Length != claims.ExpectedEventTypes.Length ||
                request.ProposedEvents.Where((proposed, index) => !string.Equals(
                    proposed.Type, claims.ExpectedEventTypes[index], StringComparison.Ordinal)).Any())
            {
                return CompleteFailed(claims,
                    "InvalidEvents", "Proposed events must match the decision plan's emission order and count.");
            }

            HandlerDescriptionMessage? handler = await GetHandlerAsync(
                    claims.Database, claims.HandlerName, cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (handler is null ||
                handler.Version != claims.HandlerVersion ||
                !string.Equals(handler.CommandType, claims.CommandType, StringComparison.Ordinal) ||
                !string.Equals(handler.PlanFingerprint, claims.PlanFingerprint, StringComparison.Ordinal))
            {
                return Invalidated(claims, "HandlerChanged", "The prepared decision handler has changed.");
            }

            SchemaSnapshotMessage snapshot = await Schemas(claims.Database).GetSnapshotAsync(cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            if (!SchemasMatch(snapshot, claims.SchemaFingerprints))
            {
                return Invalidated(claims, "SchemaChanged", "A schema used by the prepared decision has changed.");
            }

            Dictionary<string, SchemaRegistrationMessage> registrations = snapshot.EventSchemas.ToDictionary(
                value => value.Name, StringComparer.Ordinal);
            List<CandidateEventMessage> candidates = new(request.ProposedEvents.Length);
            foreach (ProposedDecisionEventMessage proposed in request.ProposedEvents)
            {
                if (!registrations.TryGetValue(proposed.Type, out SchemaRegistrationMessage? registration))
                {
                    return Invalidated(
                        claims, "EventSchemaMissing", $"Event schema '{proposed.Type}' is not registered.");
                }

                using JsonDocument payload = ParseObject(proposed.DataJson, "Proposed event JSON must be an object.");
                RegisteredJsonSchema schema = RegisteredJsonSchema.Parse(
                    registration.Name, registration.DocumentJson, eventSchema: true);
                EventKey[] keys = schema.ExtractKeys(payload.RootElement);
                candidates.Add(new CandidateEventMessage(
                    proposed.Type,
                    payload.RootElement.GetRawText(),
                    keys.Select(value => new EventKeyMessage(value.Name, value.Value)).ToArray(),
                    registration.Version));
            }

            if (remoteDecisions.UtcNow >= claims.ExpiresUtc)
            {
                return new CompleteDecisionResultMessage(
                    CompleteDecisionOutcome.Expired,
                    claims.CommandId,
                    claims.CommandType,
                    [],
                    ExpiresUtc: claims.ExpiresUtc);
            }

            AppendResultMessage append = await grains.GetGrain<IMainWriterGrain>(claims.Database).AppendAsync(
                    new EventBatchMessage(claims.CommandId, claims.CommandType, candidates.ToArray()),
                    new AppendConditionMessage(claims.Query, claims.ObservedHead),
                    cancellationToken)
                .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
            return append.Outcome switch
            {
                AppendResultOutcome.Conflict => new CompleteDecisionResultMessage(
                    CompleteDecisionOutcome.Stale,
                    claims.CommandId,
                    claims.CommandType,
                    [],
                    CurrentHead: append.Head),
                AppendResultOutcome.AlreadyCommitted => new CompleteDecisionResultMessage(
                    CompleteDecisionOutcome.AlreadyCommitted,
                    claims.CommandId,
                    append.Events[0].CommandType,
                    append.Events),
                _ => new CompleteDecisionResultMessage(
                    CompleteDecisionOutcome.Committed,
                    claims.CommandId,
                    claims.CommandType,
                    append.Events)
            };
        }
        catch (OperationCanceledException) when (cancellationToken.CancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or FormatException
                                               or OverflowException or InvalidOperationException)
        {
            return CompleteFailed(claims, "InvalidEvents", exception.Message);
        }
        catch (Exception exception)
        {
            return CompleteException(claims, exception);
        }
    }

    private IReadGrain Reader(string database) => grains.GetGrain<IReadGrain>(database);
    private ISchemaGrain Schemas(string database) => grains.GetGrain<ISchemaGrain>(database);

    private async Task<HandlerDescriptionMessage?> GetHandlerAsync(
        string database,
        string name,
        GrainCancellationToken cancellationToken)
    {
        return await grains.GetGrain<IHandlerGrain>(database).GetAsync(
                new GetHandlerMessage(name, IncludePlanJson: true, GenerateNdl: false), cancellationToken)
            .ConfigureAwait(ConfigureAwaitOptions.ContinueOnCapturedContext);
    }

    private bool ValidKey(string database, Guid commandId)
    {
        return commandId != Guid.Empty && string.Equals(
            this.GetPrimaryKeyString(), $"{database}|{commandId:D}", StringComparison.Ordinal);
    }

    private static HandlerCatalogEntry ToCatalogEntry(HandlerDescriptionMessage handler)
    {
        return new HandlerCatalogEntry(
            handler.Name,
            handler.CommandType,
            handler.NdlSource,
            handler.SourceFingerprint,
            handler.PlanFingerprint,
            handler.PlanJson ?? throw new InvalidDataException($"Handler '{handler.Name}' has no decision plan."),
            handler.Version);
    }

    private static JsonDocument ParseObject(byte[] json, string message)
    {
        JsonDocument document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Object)
        {
            return document;
        }

        document.Dispose();
        throw new ArgumentException(message);
    }

    private static void ValidateCommand(
        string commandType,
        JsonElement command,
        SchemaSnapshotMessage snapshot)
    {
        SchemaRegistrationMessage? registration = snapshot.CommandSchemas.SingleOrDefault(
            value => string.Equals(value.Name, commandType, StringComparison.Ordinal));
        if (registration is not null)
        {
            RegisteredJsonSchema.Parse(
                registration.Name, registration.DocumentJson, eventSchema: false).Validate(command);
        }
    }

    private static Dictionary<string, RegisteredJsonSchema> ParseEventSchemas(SchemaSnapshotMessage snapshot)
    {
        return snapshot.EventSchemas.ToDictionary(
            value => value.Name,
            value => RegisteredJsonSchema.Parse(value.Name, value.DocumentJson, eventSchema: true),
            StringComparer.Ordinal);
    }

    private static RemoteSchemaFingerprintMessage[] RelevantSchemas(
        SchemaSnapshotMessage snapshot,
        DecisionPlan plan)
    {
        Dictionary<string, SchemaRegistrationMessage> commands = snapshot.CommandSchemas.ToDictionary(
            value => value.Name, StringComparer.Ordinal);
        Dictionary<string, SchemaRegistrationMessage> events = snapshot.EventSchemas.ToDictionary(
            value => value.Name, StringComparer.Ordinal);
        commands.TryGetValue(plan.CommandSchema, out SchemaRegistrationMessage? command);
        IEnumerable<RemoteSchemaFingerprintMessage> relevantEvents = plan.Includes
            .Select(value => value.EventType)
            .Concat(plan.Emissions.Select(value => value.EventType))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal)
            .Select(name =>
            {
                events.TryGetValue(name, out SchemaRegistrationMessage? registration);
                return new RemoteSchemaFingerprintMessage(
                    ActorSchemaKind.Event,
                    name,
                    registration?.Fingerprint,
                    registration?.Version ?? 0);
            });
        return
        [
            new RemoteSchemaFingerprintMessage(
                ActorSchemaKind.Command,
                plan.CommandSchema,
                command?.Fingerprint,
                command?.Version ?? 0),
            .. relevantEvents
        ];
    }

    private static bool SchemasMatch(
        SchemaSnapshotMessage snapshot,
        IEnumerable<RemoteSchemaFingerprintMessage> expected)
    {
        foreach (RemoteSchemaFingerprintMessage schema in expected)
        {
            SchemaRegistrationMessage? current = schema.Kind switch
            {
                ActorSchemaKind.Command => snapshot.CommandSchemas.SingleOrDefault(
                    value => string.Equals(value.Name, schema.Name, StringComparison.Ordinal)),
                ActorSchemaKind.Event => snapshot.EventSchemas.SingleOrDefault(
                    value => string.Equals(value.Name, schema.Name, StringComparison.Ordinal)),
                _ => null
            };
            if (schema.Kind is not (ActorSchemaKind.Command or ActorSchemaKind.Event))
            {
                return false;
            }

            if (current is null)
            {
                if (schema.Version != 0 || schema.Fingerprint is not null)
                {
                    return false;
                }

                continue;
            }

            if (current.Version != schema.Version ||
                !string.Equals(current?.Fingerprint, schema.Fingerprint, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string Hash(byte[] value)
    {
        return Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();
    }

    private static ExecuteDecisionResultMessage ExecuteException(
        ExecuteDecisionMessage request,
        Exception exception)
    {
        Exception root = exception.GetBaseException();
        return root switch
        {
            DirectoryNotFoundException or KeyNotFoundException => ExecuteError(
                request, DecisionCallErrorKind.NotFound, "NotFound", root.Message),
            InvalidDataException => ExecuteError(
                request, DecisionCallErrorKind.DataLoss, "DataLoss", root.Message),
            IOException or UnauthorizedAccessException or EventStoreUnavailableException => ExecuteError(
                request, DecisionCallErrorKind.Unavailable, "WriterUnavailable", root.Message),
            _ => ExecuteError(request, DecisionCallErrorKind.None, "InvalidCommand", root.Message)
        };
    }

    private static PrepareDecisionResultMessage PrepareException(
        PrepareDecisionMessage request,
        Exception exception)
    {
        Exception root = exception.GetBaseException();
        return root switch
        {
            DirectoryNotFoundException or KeyNotFoundException => PrepareError(
                request, DecisionCallErrorKind.NotFound, "NotFound", root.Message),
            InvalidDataException => PrepareError(
                request, DecisionCallErrorKind.DataLoss, "DataLoss", root.Message),
            IOException or UnauthorizedAccessException or EventStoreUnavailableException => PrepareError(
                request, DecisionCallErrorKind.Unavailable, "WriterUnavailable", root.Message),
            _ => PrepareError(request, DecisionCallErrorKind.None, "InvalidCommand", root.Message)
        };
    }

    private static CompleteDecisionResultMessage CompleteException(
        RemoteDecisionClaimsMessage claims,
        Exception exception)
    {
        Exception root = exception.GetBaseException();
        return root switch
        {
            DirectoryNotFoundException or KeyNotFoundException => CompleteError(
                claims, DecisionCallErrorKind.NotFound, "NotFound", root.Message),
            InvalidDataException => CompleteError(
                claims, DecisionCallErrorKind.DataLoss, "DataLoss", root.Message),
            IOException or UnauthorizedAccessException or EventStoreUnavailableException => CompleteError(
                claims, DecisionCallErrorKind.Unavailable, "WriterUnavailable", root.Message),
            _ => CompleteError(claims, DecisionCallErrorKind.Unavailable, "WriterUnavailable", root.Message)
        };
    }

    private static ExecuteDecisionResultMessage ExecuteError(
        ExecuteDecisionMessage request,
        DecisionCallErrorKind kind,
        string code,
        string message)
    {
        return new ExecuteDecisionResultMessage(
            ExecuteDecisionOutcome.Failed, request.CommandId, string.Empty, [], code, message, kind);
    }

    private static PrepareDecisionResultMessage PrepareError(
        PrepareDecisionMessage request,
        DecisionCallErrorKind kind,
        string code,
        string message)
    {
        return new PrepareDecisionResultMessage(
            PrepareDecisionOutcome.Failed,
            request.CommandId,
            string.Empty,
            [],
            [],
            [],
            default,
            string.Empty,
            code,
            message,
            kind);
    }

    private static CompleteDecisionResultMessage CompleteError(
        RemoteDecisionClaimsMessage claims,
        DecisionCallErrorKind kind,
        string code,
        string message)
    {
        return new CompleteDecisionResultMessage(
            CompleteDecisionOutcome.Failed,
            claims.CommandId,
            claims.CommandType,
            [],
            Code: code,
            Message: message,
            ErrorKind: kind);
    }

    private static CompleteDecisionResultMessage CompleteFailed(
        RemoteDecisionClaimsMessage claims,
        string code,
        string message)
    {
        return CompleteError(claims, DecisionCallErrorKind.None, code, message);
    }

    private static CompleteDecisionResultMessage Invalidated(
        RemoteDecisionClaimsMessage claims,
        string code,
        string message)
    {
        return new CompleteDecisionResultMessage(
            CompleteDecisionOutcome.Invalidated,
            claims.CommandId,
            claims.CommandType,
            [],
            Code: code,
            Message: message);
    }
}