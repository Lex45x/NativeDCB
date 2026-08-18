using System.Runtime.CompilerServices;
using System.Text.Json;

using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;

using NativeDCB.Protocol.V1;

namespace NativeDCB.Web.Grpc;

public sealed class NativeDcbConsole : INativeDcbConsole
{
    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };

    private readonly AdministrationService.AdministrationServiceClient _administration;
    private readonly CatalogService.CatalogServiceClient _catalog;
    private readonly CommandService.CommandServiceClient _command;
    private readonly DatabaseService.DatabaseServiceClient _database;
    private readonly EventService.EventServiceClient _event;
    private readonly StatementService.StatementServiceClient _statement;

    public NativeDcbConsole(GrpcChannel channel)
    {
        ArgumentNullException.ThrowIfNull(channel);
        _database = new DatabaseService.DatabaseServiceClient(channel);
        _catalog = new CatalogService.CatalogServiceClient(channel);
        _command = new CommandService.CommandServiceClient(channel);
        _event = new EventService.EventServiceClient(channel);
        _statement = new StatementService.StatementServiceClient(channel);
        _administration = new AdministrationService.AdministrationServiceClient(channel);
    }

    public Task<string> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        return UnaryAsync(
            _database.ListDatabasesAsync(new ListDatabasesRequest(), cancellationToken: cancellationToken));
    }

    public Task<string> CreateDatabaseAsync(string database, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_database.CreateDatabaseAsync(
            new CreateDatabaseRequest { Database = Required(database, nameof(database)) },
            cancellationToken: cancellationToken));
    }

    public Task<string> GetDatabaseInfoAsync(string database, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_database.GetDatabaseInfoAsync(
            new GetDatabaseInfoRequest { Database = Required(database, nameof(database)) },
            cancellationToken: cancellationToken));
    }

    public Task<string> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_database.GetHealthAsync(new GetHealthRequest(), cancellationToken: cancellationToken));
    }

    public Task<string> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_database.GetCapabilitiesAsync(new GetCapabilitiesRequest(),
            cancellationToken: cancellationToken));
    }

    public Task<string> GetHeadAsync(string database, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_database.GetHeadAsync(
            new GetHeadRequest { Database = Required(database, nameof(database)) },
            cancellationToken: cancellationToken));
    }

    public Task<string> RegisterEventSchemaAsync(
        string database,
        string name,
        string schemaJson,
        bool allowIncompatible,
        CancellationToken cancellationToken = default)
    {
        return RegisterSchemaAsync(database, name, schemaJson, allowIncompatible, eventSchema: true, cancellationToken);
    }

    public Task<string> RegisterCommandSchemaAsync(
        string database,
        string name,
        string schemaJson,
        bool allowIncompatible,
        CancellationToken cancellationToken = default)
    {
        return RegisterSchemaAsync(database, name, schemaJson, allowIncompatible, eventSchema: false,
            cancellationToken);
    }

    public Task<string> RemoveSchemaAsync(
        string database,
        string name,
        string kind,
        CancellationToken cancellationToken = default)
    {
        SchemaKind schemaKind = ParseSchemaKind(kind, allowUnspecified: false);
        return UnaryAsync(_catalog.RemoveSchemaAsync(
            new RemoveSchemaRequest
            {
                Database = Required(database, nameof(database)),
                SchemaName = Required(name, nameof(name)),
                SchemaKind = schemaKind
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> GetSchemaAsync(
        string database,
        string name,
        string kind,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_catalog.GetSchemaAsync(
            new GetSchemaRequest
            {
                Database = Required(database, nameof(database)),
                SchemaName = Required(name, nameof(name)),
                SchemaKind = ParseSchemaKind(kind, allowUnspecified: false)
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> ListSchemasAsync(
        string database,
        string kind,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_catalog.ListSchemasAsync(
            new ListSchemasRequest
            {
                Database = Required(database, nameof(database)),
                SchemaKind = ParseSchemaKind(kind, allowUnspecified: true)
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> RegisterHandlerAsync(
        string database,
        string name,
        string commandType,
        string ndlSource,
        string planJson,
        bool allowIncompatible,
        CancellationToken cancellationToken = default)
    {
        bool hasNdl = !string.IsNullOrWhiteSpace(ndlSource);
        bool hasPlan = !string.IsNullOrWhiteSpace(planJson);
        if (hasNdl == hasPlan)
        {
            throw new ArgumentException("Provide either NDL source or plan JSON, but not both.");
        }

        RegisterHandlerRequest request = new()
        {
            Database = Required(database, nameof(database)),
            HandlerName = Required(name, nameof(name)),
            CommandType = Required(commandType, nameof(commandType)),
            AllowIncompatible = allowIncompatible
        };
        if (hasNdl)
        {
            request.NdlSource = ndlSource;
        }
        else
        {
            request.PlanJson = JsonBytes(planJson, nameof(planJson));
        }

        return UnaryAsync(_catalog.RegisterHandlerAsync(request, cancellationToken: cancellationToken));
    }

    public Task<string> RemoveHandlerAsync(string database, string name, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_catalog.RemoveHandlerAsync(
            new RemoveHandlerRequest
            {
                Database = Required(database, nameof(database)),
                HandlerName = Required(name, nameof(name))
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> GetHandlerAsync(
        string database,
        string name,
        bool includePlanJson,
        bool generateNdl,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_catalog.GetHandlerAsync(
            new GetHandlerRequest
            {
                Database = Required(database, nameof(database)),
                HandlerName = Required(name, nameof(name)),
                IncludePlanJson = includePlanJson,
                GenerateNdl = generateNdl
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> ListHandlersAsync(string database, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_catalog.ListHandlersAsync(
            new ListHandlersRequest { Database = Required(database, nameof(database)) },
            cancellationToken: cancellationToken));
    }

    public Task<string> ValidateNdlAsync(
        string database,
        string ndlSource,
        string transientSchemasJson,
        CancellationToken cancellationToken = default)
    {
        ValidateNdlRequest request = new()
        {
            Database = Required(database, nameof(database)),
            NdlSource = Required(ndlSource, nameof(ndlSource))
        };
        request.TransientSchemas.AddRange(ParseTransientSchemas(transientSchemasJson));
        return UnaryAsync(_catalog.ValidateNdlAsync(request, cancellationToken: cancellationToken));
    }

    public Task<string> ExecuteHandlerAsync(
        string database,
        string handlerName,
        string commandJson,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        ExecuteHandlerRequest request = new()
        {
            Database = Required(database, nameof(database)),
            HandlerName = Required(handlerName, nameof(handlerName)),
            CommandJson = JsonBytes(commandJson, nameof(commandJson))
        };
        if (!string.IsNullOrWhiteSpace(commandId))
        {
            request.CommandId = Guid.TryParse(commandId, out Guid parsed)
                ? parsed.ToString("D")
                : throw new ArgumentException("Command ID must be a UUID.", nameof(commandId));
        }

        return UnaryAsync(_command.ExecuteHandlerAsync(request, cancellationToken: cancellationToken));
    }

    public Task<string> PrepareDecisionAsync(
        string database,
        string handlerName,
        string commandJson,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        PrepareDecisionRequest request = new()
        {
            Database = Required(database, nameof(database)),
            HandlerName = Required(handlerName, nameof(handlerName)),
            CommandJson = JsonBytes(commandJson, nameof(commandJson))
        };
        if (!string.IsNullOrWhiteSpace(commandId))
        {
            request.CommandId = Guid.TryParse(commandId, out Guid parsed)
                ? parsed.ToString("D")
                : throw new ArgumentException("Command ID must be a UUID.", nameof(commandId));
        }

        return UnaryAsync(_command.PrepareDecisionAsync(request, cancellationToken: cancellationToken));
    }

    public Task<string> CompleteDecisionAsync(
        string database,
        string modelSignature,
        string proposedEventsJson,
        CancellationToken cancellationToken = default)
    {
        CompleteDecisionRequest request = new()
        {
            Database = Required(database, nameof(database)),
            ModelSignature = Base64Bytes(modelSignature, nameof(modelSignature))
        };
        request.ProposedEvents.AddRange(ParseProposedEvents(proposedEventsJson));
        return UnaryAsync(_command.CompleteDecisionAsync(request, cancellationToken: cancellationToken));
    }

    public Task<string> GetEventsByCommandIdAsync(
        string database,
        string commandId,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_command.GetEventsByCommandIdAsync(
            new GetEventsByCommandIdRequest
            {
                Database = Required(database, nameof(database)),
                CommandId = Required(commandId, nameof(commandId))
            },
            cancellationToken: cancellationToken));
    }

    public IAsyncEnumerable<string> ReadEventsByRangeAsync(
        string database,
        long afterEventId,
        long? throughEventId,
        uint? limit,
        bool follow,
        CancellationToken cancellationToken = default)
    {
        ReadEventsByRangeRequest request = new()
        {
            Database = Required(database, nameof(database)),
            AfterEventId = NonNegative(afterEventId, nameof(afterEventId)),
            Mode = follow ? ReadMode.Follow : ReadMode.Snapshot
        };
        SetRange(request, throughEventId, limit);
        return StreamAsync(_event.ReadEventsByRange(request, cancellationToken: cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<string> ReadEventsByQueryAsync(
        string database,
        string eventTypes,
        string keys,
        long afterEventId,
        long? throughEventId,
        uint? limit,
        bool committedScan,
        CancellationToken cancellationToken = default)
    {
        ReadEventsByQueryRequest request = new()
        {
            Database = Required(database, nameof(database)),
            Query = BuildQuery(eventTypes, keys, requireFilter: true),
            AfterEventId = NonNegative(afterEventId, nameof(afterEventId)),
            Consistency = Consistency(committedScan)
        };
        SetRange(request, throughEventId, limit);
        return StreamAsync(_event.ReadEventsByQuery(request, cancellationToken: cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<string> ReadEventsByTypeAndKeysAsync(
        string database,
        string eventType,
        string keys,
        long afterEventId,
        long? throughEventId,
        uint? limit,
        bool committedScan,
        CancellationToken cancellationToken = default)
    {
        ReadEventsByTypeAndKeysRequest request = new()
        {
            Database = Required(database, nameof(database)),
            EventType = Required(eventType, nameof(eventType)),
            AfterEventId = NonNegative(afterEventId, nameof(afterEventId)),
            Consistency = Consistency(committedScan)
        };
        request.Keys.AddRange(ParseKeys(keys));
        SetRange(request, throughEventId, limit);
        return StreamAsync(_event.ReadEventsByTypeAndKeys(request, cancellationToken: cancellationToken),
            cancellationToken);
    }

    public IAsyncEnumerable<string> SubscribeEventsAsync(
        string database,
        long afterEventId,
        string eventTypes,
        string queryKeys,
        string subscriptionKeys,
        CancellationToken cancellationToken = default)
    {
        SubscribeEventsRequest request = new()
        {
            Database = Required(database, nameof(database)),
            AfterEventId = NonNegative(afterEventId, nameof(afterEventId)),
            Query = BuildQuery(eventTypes, queryKeys, requireFilter: false)
        };
        request.Keys.AddRange(ParseKeys(subscriptionKeys));
        return StreamAsync(_event.SubscribeEvents(request, cancellationToken: cancellationToken), cancellationToken);
    }

    public IAsyncEnumerable<string> ExecuteStatementAsync(
        string database,
        string ndlSource,
        bool allowIncompatible,
        CancellationToken cancellationToken = default)
    {
        return StreamAsync(_statement.ExecuteStatement(
            new ExecuteStatementRequest
            {
                Database = Required(database, nameof(database)),
                NdlSource = Required(ndlSource, nameof(ndlSource)),
                AllowIncompatible = allowIncompatible
            },
            cancellationToken: cancellationToken), cancellationToken);
    }

    public Task<string> ExplainStatementAsync(
        string database,
        string ndlSource,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_statement.ExplainStatementAsync(
            new ExplainStatementRequest
            {
                Database = Required(database, nameof(database)),
                NdlSource = Required(ndlSource, nameof(ndlSource))
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> ListPartitionsAsync(string database, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_administration.ListPartitionsAsync(
            new ListPartitionsRequest { Database = Required(database, nameof(database)) },
            cancellationToken: cancellationToken));
    }

    public Task<string> ListIndexesAsync(string database, CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_administration.ListIndexesAsync(
            new ListIndexesRequest { Database = Required(database, nameof(database)) },
            cancellationToken: cancellationToken));
    }

    public Task<string> GetStateFileStatusAsync(
        string database,
        uint partitionNumber,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_administration.GetStateFileStatusAsync(
            new GetStateFileStatusRequest
            {
                Database = Required(database, nameof(database)),
                PartitionNumber = partitionNumber
            },
            cancellationToken: cancellationToken));
    }

    public Task<string> RequestIndexRebuildAsync(
        string database,
        string eventType,
        string keys,
        CancellationToken cancellationToken = default)
    {
        RequestIndexRebuildRequest request = new()
        {
            Database = Required(database, nameof(database)),
            EventType = Required(eventType, nameof(eventType))
        };
        request.Keys.AddRange(ParseKeys(keys));
        return UnaryAsync(_administration.RequestIndexRebuildAsync(request, cancellationToken: cancellationToken));
    }

    public Task<string> RequestStateRebuildAsync(
        string database,
        uint partitionNumber,
        CancellationToken cancellationToken = default)
    {
        return UnaryAsync(_administration.RequestStateRebuildAsync(
            new RequestStateRebuildRequest
            {
                Database = Required(database, nameof(database)),
                PartitionNumber = partitionNumber
            },
            cancellationToken: cancellationToken));
    }

    private Task<string> RegisterSchemaAsync(
        string database,
        string name,
        string schemaJson,
        bool allowIncompatible,
        bool eventSchema,
        CancellationToken cancellationToken)
    {
        RegisterSchemaRequest request = new()
        {
            Database = Required(database, nameof(database)),
            SchemaName = Required(name, nameof(name)),
            SchemaDocumentJson = JsonBytes(schemaJson, nameof(schemaJson)),
            AllowIncompatible = allowIncompatible
        };
        return eventSchema
            ? UnaryAsync(_catalog.RegisterEventSchemaAsync(request, cancellationToken: cancellationToken))
            : UnaryAsync(_catalog.RegisterCommandSchemaAsync(request, cancellationToken: cancellationToken));
    }

    private static async Task<string> UnaryAsync<T>(AsyncUnaryCall<T> call) where T : IMessage<T>
    {
        using (call)
        {
            return Format(await call.ResponseAsync);
        }
    }

    private static async IAsyncEnumerable<string> StreamAsync<T>(
        AsyncServerStreamingCall<T> call,
        [EnumeratorCancellation] CancellationToken cancellationToken) where T : IMessage<T>
    {
        using (call)
        {
            while (await call.ResponseStream.MoveNext(cancellationToken))
            {
                yield return Format(call.ResponseStream.Current);
            }
        }
    }

    private static Query BuildQuery(string eventTypes, string keys, bool requireFilter)
    {
        QueryItem item = new();
        item.EventTypes.AddRange(eventTypes.Split(separator: ',',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        item.Keys.AddRange(ParseKeys(keys));
        if (requireFilter && item.EventTypes.Count == 0 && item.Keys.Count == 0)
        {
            throw new ArgumentException("Enter at least one event type or query key.");
        }

        Query query = new();
        if (item.EventTypes.Count > 0 || item.Keys.Count > 0)
        {
            query.Items.Add(item);
        }

        return query;
    }

    private static IEnumerable<KeyValue> ParseKeys(string value)
    {
        foreach (string line in value.Split(['\r', '\n'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int separator = line.IndexOf(value: '=');
            if (separator <= 0 || separator == line.Length - 1)
            {
                throw new ArgumentException($"Key '{line}' must use key=value syntax.", nameof(value));
            }

            yield return new KeyValue { Key = line[..separator].Trim(), Value = line[(separator + 1)..].Trim() };
        }
    }

    private static IEnumerable<TransientSchema> ParseTransientSchemas(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(value);
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Transient schemas must be a JSON array.", nameof(value));
        }

        List<TransientSchema> schemas = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            string kind = item.GetProperty("kind").GetString() ?? string.Empty;
            SchemaKind schemaKind = kind.Equals("event", StringComparison.OrdinalIgnoreCase)
                ? SchemaKind.Event
                : kind.Equals("command", StringComparison.OrdinalIgnoreCase)
                    ? SchemaKind.Command
                    : throw new ArgumentException("Transient schema kind must be Event or Command.", nameof(value));
            JsonElement schema = item.GetProperty("schema");
            schemas.Add(new TransientSchema
            {
                SchemaKind = schemaKind,
                SchemaName =
                    Required(item.GetProperty("name").GetString() ?? string.Empty, "transient schema name"),
                SchemaDocumentJson = JsonBytes(schema.GetRawText(), "transient schema")
            });
        }

        return schemas;
    }

    private static IEnumerable<ProposedEvent> ParseProposedEvents(string value)
    {
        using JsonDocument document = JsonDocument.Parse(Required(value, nameof(value)));
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("Proposed events must be a JSON array.", nameof(value));
        }

        List<ProposedEvent> events = [];
        foreach (JsonElement item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object ||
                !item.TryGetProperty("type", out JsonElement typeElement) ||
                typeElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(typeElement.GetString()) ||
                !item.TryGetProperty("data", out JsonElement dataElement))
            {
                throw new ArgumentException(
                    "Each proposed event must use {\"type\":\"EventType\",\"data\":{...}} syntax.",
                    nameof(value));
            }

            events.Add(new ProposedEvent
            {
                Type = typeElement.GetString(),
                DataJson = ByteString.CopyFromUtf8(dataElement.GetRawText())
            });
        }

        return events;
    }

    private static void SetRange(ReadEventsByRangeRequest request, long? throughEventId, uint? limit)
    {
        ValidateThrough(request.AfterEventId, throughEventId);
        if (throughEventId.HasValue)
        {
            request.ThroughEventId = throughEventId.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }
    }

    private static void SetRange(ReadEventsByQueryRequest request, long? throughEventId, uint? limit)
    {
        ValidateThrough(request.AfterEventId, throughEventId);
        if (throughEventId.HasValue)
        {
            request.ThroughEventId = throughEventId.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }
    }

    private static void SetRange(ReadEventsByTypeAndKeysRequest request, long? throughEventId, uint? limit)
    {
        ValidateThrough(request.AfterEventId, throughEventId);
        if (throughEventId.HasValue)
        {
            request.ThroughEventId = throughEventId.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }
    }

    private static void ValidateThrough(long afterEventId, long? throughEventId)
    {
        if (throughEventId <= afterEventId)
        {
            throw new ArgumentOutOfRangeException(nameof(throughEventId), "Through ID must be greater than after ID.");
        }
    }

    private static QueryConsistency Consistency(bool committedScan)
    {
        return committedScan ? QueryConsistency.CommittedScan : QueryConsistency.EventualIndex;
    }

    private static SchemaKind ParseSchemaKind(string value, bool allowUnspecified)
    {
        if (value.Equals("event", StringComparison.OrdinalIgnoreCase))
        {
            return SchemaKind.Event;
        }

        if (value.Equals("command", StringComparison.OrdinalIgnoreCase))
        {
            return SchemaKind.Command;
        }

        if (allowUnspecified &&
            (string.IsNullOrWhiteSpace(value) || value.Equals("all", StringComparison.OrdinalIgnoreCase)))
        {
            return SchemaKind.Unspecified;
        }

        throw new ArgumentException(
            allowUnspecified ? "Schema kind must be All, Event, or Command." : "Schema kind must be Event or Command.",
            nameof(value));
    }

    private static ByteString JsonBytes(string value, string parameterName)
    {
        Required(value, parameterName);
        using (JsonDocument.Parse(value)) { }

        return ByteString.CopyFromUtf8(value);
    }

    private static ByteString Base64Bytes(string value, string parameterName)
    {
        Required(value, parameterName);
        try
        {
            return ByteString.CopyFrom(Convert.FromBase64String(value));
        }
        catch (FormatException exception)
        {
            throw new ArgumentException("Model signature must be base64.", parameterName, exception);
        }
    }

    private static string Format(IMessage message)
    {
        string json = JsonFormatter.Default.Format(message);
        using JsonDocument document = JsonDocument.Parse(json);
        return JsonSerializer.Serialize(document.RootElement, IndentedJson);
    }

    private static long NonNegative(long value, string parameterName)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(value, parameterName);
        return value;
    }

    private static string Required(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }

        return value;
    }
}