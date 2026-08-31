using System.Runtime.CompilerServices;
using System.Text.Json;

using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;

using NativeDCB.Model.Decisions;
using NativeDCB.Model.Events;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Schemas;

using ModelQuery = NativeDCB.Model.Queries.EventQuery;
using ProtocolQuery = NativeDCB.Protocol.V1.Query;
using QueryItem = NativeDCB.Model.Queries.QueryItem;

// Public client operations and request builders are consumed by applications outside this solution.
// ReSharper disable UnusedMember.Global
// ReSharper disable MemberCanBePrivate.Global
namespace NativeDCB.Sdk.Client;

public sealed class NativeDcbClient : IDisposable
{
    private readonly AdministrationService.AdministrationServiceClient? _administrationClient;
    private readonly AuditService.AuditServiceClient? _auditClient;
    private readonly AuthenticationService.AuthenticationServiceClient? _authenticationClient;
    private readonly CatalogService.CatalogServiceClient _catalogClient;
    private readonly CommandService.CommandServiceClient _commandClient;
    private readonly DatabaseService.DatabaseServiceClient? _databaseClient;
    private readonly EventService.EventServiceClient _eventClient;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly GrpcChannel? _ownedChannel;
    private readonly StatementService.StatementServiceClient? _statementClient;

    public NativeDcbClient(string address, JsonSerializerOptions? jsonOptions = null)
        : this(address, credentials: null, jsonOptions, createChannel: true)
    {
    }

    public NativeDcbClient(
        string address,
        NativeDcbCredentials credentials,
        JsonSerializerOptions? jsonOptions = null)
        : this(address, credentials ?? throw new ArgumentNullException(nameof(credentials)), jsonOptions,
            createChannel: true)
    {
    }

    private NativeDcbClient(
        string address,
        NativeDcbCredentials? credentials,
        JsonSerializerOptions? jsonOptions,
        bool createChannel)
    {
        _ = createChannel;
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        GrpcChannelOptions channelOptions = new();
        if (credentials is not null)
        {
            channelOptions.HttpHandler = new NativeDcbAuthenticationHandler(credentials, new HttpClientHandler());
        }

        _ownedChannel = GrpcChannel.ForAddress(address, channelOptions);
        _commandClient = new CommandService.CommandServiceClient(_ownedChannel);
        _eventClient = new EventService.EventServiceClient(_ownedChannel);
        _catalogClient = new CatalogService.CatalogServiceClient(_ownedChannel);
        _databaseClient = new DatabaseService.DatabaseServiceClient(_ownedChannel);
        _statementClient = new StatementService.StatementServiceClient(_ownedChannel);
        _administrationClient = new AdministrationService.AdministrationServiceClient(_ownedChannel);
        _authenticationClient = new AuthenticationService.AuthenticationServiceClient(_ownedChannel);
        _auditClient = new AuditService.AuditServiceClient(_ownedChannel);
        _jsonOptions = jsonOptions ?? JsonSerializerOptions.Default;
    }

    public NativeDcbClient(
        DatabaseService.DatabaseServiceClient databaseClient,
        CatalogService.CatalogServiceClient catalogClient,
        CommandService.CommandServiceClient commandClient,
        EventService.EventServiceClient eventClient,
        StatementService.StatementServiceClient statementClient,
        AdministrationService.AdministrationServiceClient administrationClient,
        JsonSerializerOptions? jsonOptions = null)
        : this(commandClient, eventClient, catalogClient, jsonOptions)
    {
        _databaseClient = databaseClient ?? throw new ArgumentNullException(nameof(databaseClient));
        _statementClient = statementClient ?? throw new ArgumentNullException(nameof(statementClient));
        _administrationClient =
            administrationClient ?? throw new ArgumentNullException(nameof(administrationClient));
    }

    public NativeDcbClient(
        DatabaseService.DatabaseServiceClient databaseClient,
        CatalogService.CatalogServiceClient catalogClient,
        CommandService.CommandServiceClient commandClient,
        EventService.EventServiceClient eventClient,
        StatementService.StatementServiceClient statementClient,
        AdministrationService.AdministrationServiceClient administrationClient,
        AuthenticationService.AuthenticationServiceClient authenticationClient,
        JsonSerializerOptions? jsonOptions = null)
        : this(databaseClient, catalogClient, commandClient, eventClient, statementClient, administrationClient,
            jsonOptions)
    {
        _authenticationClient = authenticationClient ?? throw new ArgumentNullException(nameof(authenticationClient));
    }

    public NativeDcbClient(
        DatabaseService.DatabaseServiceClient databaseClient,
        CatalogService.CatalogServiceClient catalogClient,
        CommandService.CommandServiceClient commandClient,
        EventService.EventServiceClient eventClient,
        StatementService.StatementServiceClient statementClient,
        AdministrationService.AdministrationServiceClient administrationClient,
        AuthenticationService.AuthenticationServiceClient authenticationClient,
        AuditService.AuditServiceClient auditClient,
        JsonSerializerOptions? jsonOptions = null)
        : this(databaseClient, catalogClient, commandClient, eventClient, statementClient, administrationClient,
            authenticationClient, jsonOptions)
    {
        _auditClient = auditClient ?? throw new ArgumentNullException(nameof(auditClient));
    }

    public NativeDcbClient(
        CommandService.CommandServiceClient commandClient,
        EventService.EventServiceClient eventClient,
        CatalogService.CatalogServiceClient catalogClient,
        JsonSerializerOptions? jsonOptions = null)
    {
        _commandClient = commandClient ?? throw new ArgumentNullException(nameof(commandClient));
        _eventClient = eventClient ?? throw new ArgumentNullException(nameof(eventClient));
        _catalogClient = catalogClient ?? throw new ArgumentNullException(nameof(catalogClient));
        _jsonOptions = jsonOptions ?? JsonSerializerOptions.Default;
    }

    public void Dispose()
    {
        _ownedChannel?.Dispose();
    }

    public async Task<CreateApiKeyResponse> CreateApiKeyAsync(
        string label,
        IEnumerable<string> permissions,
        DateTimeOffset? expiresUtc = null,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(label, nameof(label));
        ArgumentNullException.ThrowIfNull(permissions);
        CreateApiKeyRequest request = new() { Label = label };
        request.Permissions.AddRange(permissions);
        if (expiresUtc is not null)
        {
            request.ExpiresUtc = Google.Protobuf.WellKnownTypes.Timestamp.FromDateTimeOffset(expiresUtc.Value);
        }

        return await Require(_authenticationClient, nameof(AuthenticationService)).CreateApiKeyAsync(
            request, cancellationToken: cancellationToken);
    }

    public async Task<ListApiKeysResponse> ListApiKeysAsync(CancellationToken cancellationToken = default)
    {
        return await Require(_authenticationClient, nameof(AuthenticationService)).ListApiKeysAsync(
            new ListApiKeysRequest(), cancellationToken: cancellationToken);
    }

    public async Task<RevokeApiKeyResponse> RevokeApiKeyAsync(
        string keyId,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(keyId, nameof(keyId));
        return await Require(_authenticationClient, nameof(AuthenticationService)).RevokeApiKeyAsync(
            new RevokeApiKeyRequest { KeyId = keyId }, cancellationToken: cancellationToken);
    }

    public async Task<ListAuditRecordsResponse> ListAuditRecordsAsync(
        long afterSequence = 0,
        uint limit = 0,
        string? database = null,
        string? operation = null,
        string? phase = null,
        string? outcome = null,
        string? authenticationScheme = null,
        string? subject = null,
        CancellationToken cancellationToken = default)
    {
        return await Require(_auditClient, nameof(AuditService)).ListAuditRecordsAsync(
            BuildListAuditRecordsRequest(afterSequence, limit, database, operation, phase, outcome,
                authenticationScheme, subject),
            cancellationToken: cancellationToken);
    }

    public async Task<ListDatabasesResponse> ListDatabasesAsync(CancellationToken cancellationToken = default)
    {
        return await Require(_databaseClient, nameof(DatabaseService)).ListDatabasesAsync(
            new ListDatabasesRequest(), cancellationToken: cancellationToken);
    }

    // ReSharper disable once UnusedMethodReturnValue.Global
    public async Task<CreateDatabaseResponse> CreateDatabaseAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        return await Require(_databaseClient, nameof(DatabaseService)).CreateDatabaseAsync(
            new CreateDatabaseRequest { Database = database }, cancellationToken: cancellationToken);
    }

    public async Task<GetDatabaseInfoResponse> GetDatabaseInfoAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        return await Require(_databaseClient, nameof(DatabaseService)).GetDatabaseInfoAsync(
            new GetDatabaseInfoRequest { Database = database }, cancellationToken: cancellationToken);
    }

    public async Task<GetHealthResponse> GetHealthAsync(CancellationToken cancellationToken = default)
    {
        return await Require(_databaseClient, nameof(DatabaseService)).GetHealthAsync(
            new GetHealthRequest(), cancellationToken: cancellationToken);
    }

    public async Task<GetCapabilitiesResponse> GetCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        return await Require(_databaseClient, nameof(DatabaseService)).GetCapabilitiesAsync(
            new GetCapabilitiesRequest(), cancellationToken: cancellationToken);
    }

    public async Task<GetHeadResponse> GetHeadAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        return await Require(_databaseClient, nameof(DatabaseService)).GetHeadAsync(
            new GetHeadRequest { Database = database }, cancellationToken: cancellationToken);
    }

    public async Task<ExecuteHandlerResponse> ExecuteHandlerAsync<TCommand>(
        string database,
        string handlerName,
        TCommand command,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        return await _commandClient.ExecuteHandlerAsync(
            BuildExecuteHandlerRequest(database, handlerName, command, commandId, _jsonOptions),
            cancellationToken: cancellationToken);
    }

    public async Task<PreparedDecision<TModel>> PrepareDecisionAsync<TCommand, TModel>(
        string database,
        string handlerName,
        TCommand command,
        Guid? commandId = null,
        CancellationToken cancellationToken = default)
    {
        PrepareDecisionResponse response = await _commandClient.PrepareDecisionAsync(
            BuildPrepareDecisionRequest(database, handlerName, command, commandId, _jsonOptions),
            cancellationToken: cancellationToken);
        return new PreparedDecision<TModel>(response, _jsonOptions);
    }

    public async Task<CompleteDecisionResponse> CompleteDecisionAsync(
        string database,
        ByteString modelSignature,
        IReadOnlyCollection<ProposedDecisionEvent> proposedEvents,
        CancellationToken cancellationToken = default)
    {
        return await _commandClient.CompleteDecisionAsync(
            BuildCompleteDecisionRequest(database, modelSignature, proposedEvents, _jsonOptions),
            cancellationToken: cancellationToken);
    }

    public async Task<GetEventsByCommandIdResponse> GetEventsByCommandIdAsync(
        string database,
        Guid commandId,
        CancellationToken cancellationToken = default)
    {
        return await _commandClient.GetEventsByCommandIdAsync(
            BuildGetEventsByCommandIdRequest(database, commandId),
            cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<SequencedEvent> ReadEventsByRangeAsync(
        string database,
        long afterEventId = 0,
        long? throughEventId = null,
        uint? limit = null,
        ReadMode mode = ReadMode.Snapshot,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using AsyncServerStreamingCall<EventEnvelope> call = _eventClient.ReadEventsByRange(
            BuildReadEventsByRangeRequest(database, afterEventId, throughEventId, limit, mode),
            cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return MapEvent(call.ResponseStream.Current);
        }
    }

    public async IAsyncEnumerable<SequencedEvent> ReadEventsByQueryAsync(
        string database,
        ModelQuery query,
        long afterEventId = 0,
        long? throughEventId = null,
        uint? limit = null,
        QueryConsistency consistency = QueryConsistency.CommittedScan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        using AsyncServerStreamingCall<EventEnvelope> call = _eventClient.ReadEventsByQuery(
            BuildReadEventsByQueryRequest(database, query, afterEventId, throughEventId, limit, consistency),
            cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return MapEvent(call.ResponseStream.Current);
        }
    }

    public async IAsyncEnumerable<SequencedEvent> ReadEventsByTypeAndKeysAsync(
        string database,
        string eventType,
        IReadOnlyCollection<EventKey> keys,
        long afterEventId = 0,
        long? throughEventId = null,
        uint? limit = null,
        QueryConsistency consistency = QueryConsistency.CommittedScan,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRange(database, afterEventId, throughEventId);
        ValidateRequired(eventType, nameof(eventType));
        ArgumentNullException.ThrowIfNull(keys);
        ReadEventsByTypeAndKeysRequest request = new()
        {
            Database = database,
            EventType = eventType,
            AfterEventId = afterEventId,
            Consistency = consistency
        };
        request.Keys.AddRange(keys.Select(key => new KeyValue { Key = key.Name, Value = key.Value }));
        if (throughEventId.HasValue)
        {
            request.ThroughEventId = throughEventId.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }

        using AsyncServerStreamingCall<EventEnvelope> call =
            _eventClient.ReadEventsByTypeAndKeys(request, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return MapEvent(call.ResponseStream.Current);
        }
    }

    public async IAsyncEnumerable<SequencedEvent> SubscribeEventsAsync(
        string database,
        long afterEventId = 0,
        ModelQuery? query = null,
        IReadOnlyCollection<EventKey>? keys = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRange(database, afterEventId, throughEventId: null);
        SubscribeEventsRequest request = new() { Database = database, AfterEventId = afterEventId };
        if (query is not null)
        {
            request.Query = MapQuery(query);
        }

        if (keys is not null)
        {
            request.Keys.AddRange(keys.Select(key => new KeyValue { Key = key.Name, Value = key.Value }));
        }

        using AsyncServerStreamingCall<EventEnvelope> call =
            _eventClient.SubscribeEvents(request, cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return MapEvent(call.ResponseStream.Current);
        }
    }

    public async Task<RegisterHandlerResponse> RegisterHandlerAsync(
        string database,
        string handlerName,
        string commandType,
        string ndlSource,
        bool allowIncompatible = false,
        CancellationToken cancellationToken = default)
    {
        return await _catalogClient.RegisterHandlerAsync(
            BuildRegisterHandlerRequest(database, handlerName, commandType, ndlSource, allowIncompatible),
            cancellationToken: cancellationToken);
    }

    public async Task<RegisterHandlerResponse> RegisterDecisionAsync<TCommand>(
        string database,
        string handlerName,
        DecisionDefinition<TCommand> definition,
        bool allowIncompatible = false,
        CancellationToken cancellationToken = default)
        where TCommand : notnull
    {
        ArgumentNullException.ThrowIfNull(definition);
        DecisionPlan plan = definition.Compile(handlerName);
        return await _catalogClient.RegisterHandlerAsync(
            BuildRegisterDecisionRequest(database, handlerName, plan, allowIncompatible),
            cancellationToken: cancellationToken);
    }

    public Task<RegisterSchemaResponse> RegisterEventSchemaAsync<TEvent>(
        string database,
        bool allowIncompatible = false,
        CancellationToken cancellationToken = default)
    {
        return RegisterSchemaAsync(
            database,
            SchemaDescriptor.ForEvent<TEvent>(),
            allowIncompatible,
            cancellationToken);
    }

    public Task<RegisterSchemaResponse> RegisterCommandSchemaAsync<TCommand>(
        string database,
        bool allowIncompatible = false,
        CancellationToken cancellationToken = default)
    {
        return RegisterSchemaAsync(
            database,
            SchemaDescriptor.ForCommand<TCommand>(),
            allowIncompatible,
            cancellationToken);
    }

    public async Task<ValidateNdlResponse> ValidateNdlAsync(
        string database,
        string ndlSource,
        CancellationToken cancellationToken = default)
    {
        return await _catalogClient.ValidateNdlAsync(
            BuildValidateNdlRequest(database, ndlSource),
            cancellationToken: cancellationToken);
    }

    public async Task<RemoveSchemaResponse> RemoveSchemaAsync(
        string database,
        string schemaName,
        SchemaKind schemaKind,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(schemaName, nameof(schemaName));
        return await _catalogClient.RemoveSchemaAsync(
            new RemoveSchemaRequest { Database = database, SchemaName = schemaName, SchemaKind = schemaKind },
            cancellationToken: cancellationToken);
    }

    public async Task<GetSchemaResponse> GetSchemaAsync(
        string database,
        string schemaName,
        SchemaKind schemaKind,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(schemaName, nameof(schemaName));
        if (schemaKind is not (SchemaKind.Event or SchemaKind.Command))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaKind), "An event or command schema kind is required.");
        }

        return await _catalogClient.GetSchemaAsync(
            new GetSchemaRequest { Database = database, SchemaName = schemaName, SchemaKind = schemaKind },
            cancellationToken: cancellationToken);
    }

    public async Task<ListSchemasResponse> ListSchemasAsync(
        string database,
        SchemaKind schemaKind = SchemaKind.Unspecified,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        if (schemaKind is not (SchemaKind.Unspecified or SchemaKind.Event or SchemaKind.Command))
        {
            throw new ArgumentOutOfRangeException(nameof(schemaKind), "The schema kind is invalid.");
        }

        return await _catalogClient.ListSchemasAsync(
            new ListSchemasRequest { Database = database, SchemaKind = schemaKind },
            cancellationToken: cancellationToken);
    }

    public async Task<RemoveHandlerResponse> RemoveHandlerAsync(
        string database,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(handlerName, nameof(handlerName));
        return await _catalogClient.RemoveHandlerAsync(
            new RemoveHandlerRequest { Database = database, HandlerName = handlerName },
            cancellationToken: cancellationToken);
    }

    public async Task<GetHandlerResponse> GetHandlerAsync(
        string database,
        string handlerName,
        CancellationToken cancellationToken = default)
    {
        return await GetHandlerAsync(
            database, handlerName, includePlanJson: false, generateNdl: false, cancellationToken);
    }

    public async Task<GetHandlerResponse> GetHandlerAsync(
        string database,
        string handlerName,
        bool includePlanJson,
        bool generateNdl,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(handlerName, nameof(handlerName));
        return await _catalogClient.GetHandlerAsync(
            new GetHandlerRequest
            {
                Database = database,
                HandlerName = handlerName,
                IncludePlanJson = includePlanJson,
                GenerateNdl = generateNdl
            },
            cancellationToken: cancellationToken);
    }

    public async Task<ListHandlersResponse> ListHandlersAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        return await _catalogClient.ListHandlersAsync(
            new ListHandlersRequest { Database = database }, cancellationToken: cancellationToken);
    }

    public async IAsyncEnumerable<StatementResult> ExecuteStatementAsync(
        string database,
        string ndlSource,
        bool allowIncompatible = false,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(ndlSource, nameof(ndlSource));
        using AsyncServerStreamingCall<StatementResult> call = Require(_statementClient, nameof(StatementService))
            .ExecuteStatement(
                new ExecuteStatementRequest
                {
                    Database = database,
                    NdlSource = ndlSource,
                    AllowIncompatible = allowIncompatible
                },
                cancellationToken: cancellationToken);
        while (await call.ResponseStream.MoveNext(cancellationToken))
        {
            yield return call.ResponseStream.Current;
        }
    }

    public async Task<ExplainStatementResponse> ExplainStatementAsync(
        string database,
        string ndlSource,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(ndlSource, nameof(ndlSource));
        return await Require(_statementClient, nameof(StatementService)).ExplainStatementAsync(
            new ExplainStatementRequest { Database = database, NdlSource = ndlSource },
            cancellationToken: cancellationToken);
    }

    public async Task<ListPartitionsResponse> ListPartitionsAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        return await Require(_administrationClient, nameof(AdministrationService)).ListPartitionsAsync(
            new ListPartitionsRequest { Database = database }, cancellationToken: cancellationToken);
    }

    public async Task<ListIndexesResponse> ListIndexesAsync(
        string database,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        return await Require(_administrationClient, nameof(AdministrationService)).ListIndexesAsync(
            new ListIndexesRequest { Database = database }, cancellationToken: cancellationToken);
    }

    public async Task<GetStateFileStatusResponse> GetStateFileStatusAsync(
        string database,
        uint partitionNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        if (partitionNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionNumber));
        }

        return await Require(_administrationClient, nameof(AdministrationService)).GetStateFileStatusAsync(
            new GetStateFileStatusRequest { Database = database, PartitionNumber = partitionNumber },
            cancellationToken: cancellationToken);
    }

    public async Task<RebuildResponse> RequestIndexRebuildAsync(
        string database,
        string eventType,
        IReadOnlyCollection<EventKey> keys,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(eventType, nameof(eventType));
        ArgumentNullException.ThrowIfNull(keys);
        RequestIndexRebuildRequest request = new() { Database = database, EventType = eventType };
        request.Keys.AddRange(keys.Select(key => new KeyValue { Key = key.Name, Value = key.Value }));
        return await Require(_administrationClient, nameof(AdministrationService)).RequestIndexRebuildAsync(
            request, cancellationToken: cancellationToken);
    }

    public async Task<RebuildResponse> RequestStateRebuildAsync(
        string database,
        uint partitionNumber,
        CancellationToken cancellationToken = default)
    {
        ValidateRequired(database, nameof(database));
        if (partitionNumber == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(partitionNumber));
        }

        return await Require(_administrationClient, nameof(AdministrationService)).RequestStateRebuildAsync(
            new RequestStateRebuildRequest { Database = database, PartitionNumber = partitionNumber },
            cancellationToken: cancellationToken);
    }

    public static ExecuteHandlerRequest BuildExecuteHandlerRequest<TCommand>(
        string database,
        string handlerName,
        TCommand command,
        Guid? commandId = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(handlerName, nameof(handlerName));
        ArgumentNullException.ThrowIfNull(command);
        ExecuteHandlerRequest request = new()
        {
            Database = database,
            HandlerName = handlerName,
            CommandJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(command, jsonOptions))
        };
        if (commandId.HasValue)
        {
            request.CommandId = commandId.Value.ToString("D");
        }

        return request;
    }

    public static ListAuditRecordsRequest BuildListAuditRecordsRequest(
        long afterSequence = 0,
        uint limit = 0,
        string? database = null,
        string? operation = null,
        string? phase = null,
        string? outcome = null,
        string? authenticationScheme = null,
        string? subject = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(afterSequence);
        if (limit > 1000)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "The audit page limit cannot exceed 1000.");
        }

        ListAuditRecordsRequest request = new() { AfterSequence = afterSequence, Limit = limit };
        if (database is not null)
        {
            request.Database = database;
        }

        if (operation is not null)
        {
            request.Operation = operation;
        }

        if (phase is not null)
        {
            request.Phase = phase;
        }

        if (outcome is not null)
        {
            request.Outcome = outcome;
        }

        if (authenticationScheme is not null)
        {
            request.AuthenticationScheme = authenticationScheme;
        }

        if (subject is not null)
        {
            request.Subject = subject;
        }

        return request;
    }

    public static PrepareDecisionRequest BuildPrepareDecisionRequest<TCommand>(
        string database,
        string handlerName,
        TCommand command,
        Guid? commandId = null,
        JsonSerializerOptions? jsonOptions = null)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(handlerName, nameof(handlerName));
        ArgumentNullException.ThrowIfNull(command);
        PrepareDecisionRequest request = new()
        {
            Database = database,
            HandlerName = handlerName,
            CommandJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(command, jsonOptions))
        };
        if (commandId.HasValue)
        {
            request.CommandId = commandId.Value.ToString("D");
        }

        return request;
    }

    public static CompleteDecisionRequest BuildCompleteDecisionRequest(
        string database,
        ByteString modelSignature,
        IReadOnlyCollection<ProposedDecisionEvent> proposedEvents,
        JsonSerializerOptions? jsonOptions = null)
    {
        ValidateRequired(database, nameof(database));
        ArgumentNullException.ThrowIfNull(modelSignature);
        if (modelSignature.IsEmpty)
        {
            throw new ArgumentException("A value is required.", nameof(modelSignature));
        }

        ArgumentNullException.ThrowIfNull(proposedEvents);
        CompleteDecisionRequest request = new() { Database = database, ModelSignature = modelSignature };
        foreach (ProposedDecisionEvent proposedEvent in proposedEvents)
        {
            ArgumentNullException.ThrowIfNull(proposedEvent);
            ValidateRequired(proposedEvent.Type, nameof(proposedEvents));
            ArgumentNullException.ThrowIfNull(proposedEvent.Data);
            request.ProposedEvents.Add(new ProposedEvent
            {
                Type = proposedEvent.Type,
                DataJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                    proposedEvent.Data,
                    proposedEvent.Data.GetType(),
                    jsonOptions))
            });
        }

        return request;
    }

    public static GetEventsByCommandIdRequest BuildGetEventsByCommandIdRequest(string database, Guid commandId)
    {
        ValidateRequired(database, nameof(database));
        return new GetEventsByCommandIdRequest { Database = database, CommandId = commandId.ToString("D") };
    }

    public static ReadEventsByRangeRequest BuildReadEventsByRangeRequest(
        string database,
        long afterEventId = 0,
        long? throughEventId = null,
        uint? limit = null,
        ReadMode mode = ReadMode.Snapshot)
    {
        ValidateRange(database, afterEventId, throughEventId);
        ReadEventsByRangeRequest request = new() { Database = database, AfterEventId = afterEventId, Mode = mode };
        if (throughEventId.HasValue)
        {
            request.ThroughEventId = throughEventId.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }

        return request;
    }

    public static ReadEventsByQueryRequest BuildReadEventsByQueryRequest(
        string database,
        ModelQuery query,
        long afterEventId = 0,
        long? throughEventId = null,
        uint? limit = null,
        QueryConsistency consistency = QueryConsistency.CommittedScan)
    {
        ArgumentNullException.ThrowIfNull(query);
        ValidateRange(database, afterEventId, throughEventId);
        ReadEventsByQueryRequest request = new()
        {
            Database = database,
            Query = MapQuery(query),
            AfterEventId = afterEventId,
            Consistency = consistency
        };
        if (throughEventId.HasValue)
        {
            request.ThroughEventId = throughEventId.Value;
        }

        if (limit.HasValue)
        {
            request.Limit = limit.Value;
        }

        return request;
    }

    public static RegisterHandlerRequest BuildRegisterHandlerRequest(
        string database,
        string handlerName,
        string commandType,
        string ndlSource,
        bool allowIncompatible = false)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(handlerName, nameof(handlerName));
        ValidateRequired(commandType, nameof(commandType));
        ValidateRequired(ndlSource, nameof(ndlSource));
        return new RegisterHandlerRequest
        {
            Database = database,
            HandlerName = handlerName,
            CommandType = commandType,
            NdlSource = ndlSource,
            AllowIncompatible = allowIncompatible
        };
    }

    public static RegisterSchemaRequest BuildRegisterSchemaRequest(
        string database,
        SchemaDescriptor schema,
        bool allowIncompatible = false)
    {
        ValidateRequired(database, nameof(database));
        ArgumentNullException.ThrowIfNull(schema);
        return new RegisterSchemaRequest
        {
            Database = database,
            SchemaName = schema.Name,
            SchemaDocumentJson = ByteString.CopyFromUtf8(schema.ToJsonSchemaDocument()),
            AllowIncompatible = allowIncompatible
        };
    }

    public static RegisterHandlerRequest BuildRegisterDecisionRequest(
        string database,
        string handlerName,
        DecisionPlan plan,
        bool allowIncompatible = false)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(handlerName, nameof(handlerName));
        ArgumentNullException.ThrowIfNull(plan);
        return new RegisterHandlerRequest
        {
            Database = database,
            HandlerName = handlerName,
            CommandType = plan.CommandSchema,
            AllowIncompatible = allowIncompatible,
            PlanJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
        };
    }

    public static ValidateNdlRequest BuildValidateNdlRequest(string database, string ndlSource)
    {
        ValidateRequired(database, nameof(database));
        ValidateRequired(ndlSource, nameof(ndlSource));
        return new ValidateNdlRequest { Database = database, NdlSource = ndlSource };
    }

    public static ProtocolQuery MapQuery(ModelQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);
        ProtocolQuery result = new();
        foreach (QueryItem item in query.Items)
        {
            Protocol.V1.QueryItem mapped = new();
            mapped.EventTypes.AddRange(item.EventTypes);
            mapped.Keys.AddRange(item.Keys.Select(key => new KeyValue { Key = key.Name, Value = key.Value }));
            result.Items.Add(mapped);
        }

        return result;
    }

    public static SequencedEvent MapEvent(EventEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        using JsonDocument document = JsonDocument.Parse(envelope.DataJson.Memory);
        return new SequencedEvent(
            envelope.EventId,
            envelope.Type,
            document.RootElement.Clone(),
            envelope.Keys.Select(key => new EventKey(key.Key, key.Value)).ToArray(),
            envelope.SchemaVersion,
            envelope.TimestampUtc.ToDateTimeOffset(),
            Guid.Parse(envelope.CommandId),
            envelope.CommandType);
    }

    private async Task<RegisterSchemaResponse> RegisterSchemaAsync(
        string database,
        SchemaDescriptor schema,
        bool allowIncompatible,
        CancellationToken cancellationToken)
    {
        RegisterSchemaRequest request = BuildRegisterSchemaRequest(database, schema, allowIncompatible);
        return schema.SchemaType == SchemaType.Event
            ? await _catalogClient.RegisterEventSchemaAsync(request, cancellationToken: cancellationToken)
            : await _catalogClient.RegisterCommandSchemaAsync(request, cancellationToken: cancellationToken);
    }

    private static void ValidateRange(string database, long afterEventId, long? throughEventId)
    {
        ValidateRequired(database, nameof(database));
        ArgumentOutOfRangeException.ThrowIfNegative(afterEventId);
        if (throughEventId < afterEventId)
        {
            throw new ArgumentOutOfRangeException(nameof(throughEventId),
                "The upper bound cannot precede the lower bound.");
        }
    }

    private static void ValidateRequired(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A value is required.", parameterName);
        }
    }

    private static T Require<T>(T? client, string serviceName) where T : class
    {
        return client ??
               throw new InvalidOperationException(
                   $"{serviceName} is unavailable because this client was created with the legacy three-service constructor.");
    }
}