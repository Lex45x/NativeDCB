using System.Text.Json;

using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

using NativeDCB.Model;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk;

using QueryItem = NativeDCB.Protocol.V1.QueryItem;

namespace NativeDCB.Server.IntegrationTests;

public sealed class NativeDcbServerTests : IAsyncLifetime
{
    private const string CreateCourseNdl = """
                                           decision DefineCourse
                                           from DefineCourse command
                                           | include CourseDefined event
                                               where event.CourseId == command.CourseId
                                               apply { CourseExists = true }
                                           | evaluate {
                                               require not exists(model.CourseExists) else "Course already exists";
                                           }
                                           | decide {
                                               emit CourseDefined {
                                                   CourseId = command.CourseId,
                                                   Capacity = command.Capacity
                                               };
                                           };
                                           """;

    private const string SubscribeNdl = """
                                        decision SubscribeStudentToCourse
                                        from SubscribeStudentToCourse command
                                        | include CourseDefined event
                                            where event.CourseId == command.CourseId
                                            apply {
                                                CourseExists = true,
                                                CourseCapacity = event.Capacity
                                            }
                                        | include StudentSubscribedToCourse event
                                            where event.CourseId == command.CourseId
                                            apply {
                                                CourseSubscriptions = (previous.CourseSubscriptions ?? 0) + 1
                                            }
                                        | include StudentSubscribedToCourse event
                                            where event.StudentId == command.StudentId and event.CourseId == command.CourseId
                                            apply {
                                                AlreadySubscribed = true
                                            }
                                        | evaluate {
                                            require (model.CourseExists ?? false) else "Course does not exist";
                                            require (model.CourseSubscriptions ?? 0) < model.CourseCapacity else "Course is full";
                                            require not (model.AlreadySubscribed ?? false) else "Student is already subscribed";
                                            let remainingSeats = model.CourseCapacity - (model.CourseSubscriptions ?? 0) - 1;
                                        }
                                        | decide {
                                            emit StudentSubscribedToCourse {
                                                StudentId = command.StudentId,
                                                CourseId = command.CourseId,
                                                RemainingSeats = max(remainingSeats, 0)
                                            };
                                        };
                                        """;

    private const string SchemaCompatibleSubscribeNdl = """
                                                        decision SubscribeStudent
                                                        from SubscribeStudentToCourse command
                                                        | include CourseDefined event
                                                            where event.CourseId == command.CourseId
                                                            apply { CourseExists = true }
                                                        | evaluate {
                                                            require (model.CourseExists ?? false) else "Course does not exist";
                                                        }
                                                        | decide {
                                                            emit StudentSubscribedToCourse {
                                                                StudentId = command.StudentId,
                                                                CourseId = command.CourseId
                                                            };
                                                        };
                                                        """;

    private GrpcChannel _channel = null!;

    private ServerFactory _factory = null!;

    public Task InitializeAsync()
    {
        _factory = new ServerFactory();
        _channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _channel.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Create_register_execute_and_read_over_grpc()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        EventService.EventServiceClient events = new(_channel);
        StatementService.StatementServiceClient statements = new(_channel);
        AdministrationService.AdministrationServiceClient administration = new(_channel);

        CreateDatabaseResponse created =
            await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        Assert.Equal(DatabaseState.Ready, created.Database.State);
        Assert.Equal(expected: 0, created.Database.MainHead);
        Assert.True(File.Exists(Path.Combine(_factory.DatabaseRoot, "school", "catalog_v1.json")));

        RegisterHandlerResponse define = await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        RegisterHandlerResponse subscribe = await RegisterAsync(
            catalog, "SubscribeStudent", "SubscribeStudentToCourse", SubscribeNdl);
        Assert.True(define.Handler.Valid);
        Assert.True(subscribe.Handler.Valid);

        ExplainStatementResponse explained = await statements.ExplainStatementAsync(new ExplainStatementRequest
        {
            Database = "school", NdlSource = SubscribeNdl
        });
        Assert.True(explained.Valid);
        Assert.Contains("emit:1", explained.Plan.Operations);

        ExecuteHandlerResponse course = await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 2 });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, course.OutcomeCase);
        Assert.Equal(expected: 1, course.Committed.FirstEventId);

        Guid commandId = Guid.NewGuid();
        ExecuteHandlerResponse subscribed = await ExecuteAsync(
            commands, "SubscribeStudent", commandId, new { StudentId = "student-1", CourseId = "course-1" });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, subscribed.OutcomeCase);
        Assert.Equal(expected: 2, subscribed.Committed.FirstEventId);
        Assert.Equal("StudentSubscribedToCourse", subscribed.Committed.Events.Single().Type);
        using (JsonDocument payload = JsonDocument.Parse(subscribed.Committed.Events.Single().DataJson.ToByteArray()))
        {
            Assert.Equal(expected: 1, payload.RootElement.GetProperty("RemainingSeats").GetInt32());
        }

        ExecuteHandlerResponse duplicate = await ExecuteAsync(
            commands, "SubscribeStudent", commandId, new { StudentId = "ignored", CourseId = "ignored" });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.AlreadyCommitted, duplicate.OutcomeCase);
        Assert.Equal(expected: 2, duplicate.AlreadyCommitted.FirstEventId);

        ExecuteHandlerResponse rejected = await ExecuteAsync(
            commands, "SubscribeStudent", Guid.NewGuid(), new { StudentId = "student-1", CourseId = "course-1" });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Rejected, rejected.OutcomeCase);
        Assert.Equal("DomainRejected", rejected.Rejected.Code);
        Assert.Equal("Student is already subscribed", rejected.Rejected.Message);

        IReadOnlyList<EventEnvelope> range = await ReadAllAsync(events
            .ReadEventsByRange(new ReadEventsByRangeRequest { Database = "school", AfterEventId = 0 }).ResponseStream);
        Assert.Equal([1L, 2L], range.Select(x => x.EventId));

        ReadEventsByQueryRequest queryRequest = new() { Database = "school", AfterEventId = 0 };
        QueryItem queryItem = new();
        queryItem.EventTypes.Add("StudentSubscribedToCourse");
        queryItem.Keys.Add(new KeyValue { Key = "StudentId", Value = "student-1" });
        queryRequest.Query = new Query();
        queryRequest.Query.Items.Add(queryItem);
        IReadOnlyList<EventEnvelope>
            queried = await ReadAllAsync(events.ReadEventsByQuery(queryRequest).ResponseStream);
        Assert.Single(queried);
        Assert.Equal(expected: 2, queried[index: 0].EventId);

        GetEventsByCommandIdResponse reconciled = await commands.GetEventsByCommandIdAsync(
            new GetEventsByCommandIdRequest { Database = "school", CommandId = commandId.ToString("D") });
        Assert.Single(reconciled.Events);
        Assert.Equal(expected: 2, reconciled.Events[index: 0].EventId);

        ListPartitionsResponse partitions = await administration.ListPartitionsAsync(
            new ListPartitionsRequest { Database = "school" });
        Assert.Single(partitions.Partitions);
        Assert.Equal((ulong)2, partitions.Partitions[index: 0].EventCount);

        RegisterHandlerResponse invalid = await RegisterAsync(
            catalog, "SubscribeStudent", "SubscribeStudentToCourse", "decision broken");
        Assert.False(invalid.Handler.Valid);
        Assert.Contains(invalid.Handler.Diagnostics, x => x.Severity == DiagnosticSeverity.Error);

        GetHandlerResponse retained = await catalog.GetHandlerAsync(new GetHandlerRequest
        {
            Database = "school", HandlerName = "SubscribeStudent"
        });
        Assert.Equal(subscribe.Handler.SourceFingerprint, retained.Handler.SourceFingerprint);

        _channel.Dispose();
        _factory.PreserveDatabaseRoot = true;
        await _factory.DisposeAsync();
        _factory = new ServerFactory(_factory.DatabaseRoot);
        _channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        CatalogService.CatalogServiceClient reopenedCatalog = new(_channel);
        GetHandlerResponse reopened = await reopenedCatalog.GetHandlerAsync(new GetHandlerRequest
        {
            Database = "school", HandlerName = "SubscribeStudent", IncludePlanJson = true, GenerateNdl = true
        });
        Assert.Equal(subscribe.Handler.SourceFingerprint, reopened.Handler.SourceFingerprint);
        Assert.NotEmpty(reopened.Handler.PlanJson);
        Assert.StartsWith("decision SubscribeStudentToCourse", reopened.Handler.GeneratedNdl,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Grpc_web_supports_browser_unary_and_server_streaming_calls()
    {
        using GrpcWebHandler handler = new(GrpcWebMode.GrpcWeb, _factory.Server.CreateHandler());
        using HttpClient http = new(handler);
        http.DefaultRequestHeaders.Add("Origin", "http://localhost:5094");
        using GrpcChannel channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpClient = http, DisposeHttpClient = false });
        DatabaseService.DatabaseServiceClient databases = new(channel);
        CatalogService.CatalogServiceClient catalog = new(channel);
        CommandService.CommandServiceClient commands = new(channel);
        EventService.EventServiceClient events = new(channel);

        CreateDatabaseResponse created = await databases.CreateDatabaseAsync(
            new CreateDatabaseRequest { Database = "browser" });
        Assert.Equal("browser", created.Database.Database);
        await catalog.RegisterHandlerAsync(new RegisterHandlerRequest
        {
            Database = "browser",
            HandlerName = "DefineCourse",
            CommandType = "DefineCourse",
            NdlSource = CreateCourseNdl
        });
        ExecuteHandlerResponse executed = await commands.ExecuteHandlerAsync(new ExecuteHandlerRequest
        {
            Database = "browser",
            HandlerName = "DefineCourse",
            CommandJson = ByteString.CopyFromUtf8("{\"CourseId\":\"grpc-web\",\"Capacity\":1}")
        });

        IReadOnlyList<EventEnvelope> streamed = await ReadAllAsync(events.ReadEventsByRange(
            new ReadEventsByRangeRequest { Database = "browser", AfterEventId = 0 }).ResponseStream);

        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, executed.OutcomeCase);
        Assert.Equal("CourseDefined", Assert.Single(streamed).Type);
    }

    [Fact]
    public async Task Invalid_database_path_and_missing_command_use_grpc_statuses()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);

        RpcException invalidPath = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "../outside" }));
        Assert.Equal(StatusCode.InvalidArgument, invalidPath.StatusCode);
        AssertErrorDetail(invalidPath, "InvalidArgument");

        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "empty" });
        RpcException missing = await Assert.ThrowsAsync<RpcException>(async () =>
            await commands.GetEventsByCommandIdAsync(new GetEventsByCommandIdRequest
            {
                Database = "empty", CommandId = Guid.NewGuid().ToString("D")
            }));
        Assert.Equal(StatusCode.NotFound, missing.StatusCode);
        AssertErrorDetail(missing, "NotFound");
    }

    [Fact]
    public async Task Concurrent_transactions_retry_against_the_main_writer_boundary()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        EventService.EventServiceClient events = new(_channel);

        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        await RegisterAsync(catalog, "SubscribeStudent", "SubscribeStudentToCourse", SubscribeNdl);
        ExecuteHandlerResponse course = await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 1 });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, course.OutcomeCase);

        Task<ExecuteHandlerResponse> first = ExecuteAsync(
            commands, "SubscribeStudent", Guid.NewGuid(), new { StudentId = "student-1", CourseId = "course-1" });
        Task<ExecuteHandlerResponse> second = ExecuteAsync(
            commands, "SubscribeStudent", Guid.NewGuid(), new { StudentId = "student-2", CourseId = "course-1" });
        ExecuteHandlerResponse[] results = await Task.WhenAll(first, second);

        Assert.Single(results, result => result.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Committed);
        ExecuteHandlerResponse rejected = Assert.Single(
            results, result => result.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Rejected);
        Assert.Equal("Course is full", rejected.Rejected.Message);

        IReadOnlyList<EventEnvelope> persisted = await ReadAllAsync(events.ReadEventsByRange(
            new ReadEventsByRangeRequest { Database = "school", AfterEventId = 0 }).ResponseStream);
        Assert.Equal([1L, 2L], persisted.Select(value => value.EventId));
    }

    [Fact]
    public async Task Duplicate_command_reconciles_before_handler_and_payload_validation()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);

        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        Guid commandId = Guid.NewGuid();
        ExecuteHandlerResponse committed = await ExecuteAsync(
            commands, "DefineCourse", commandId, new { CourseId = "course-1", Capacity = 1 });
        Assert.True(
            committed.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Committed,
            committed.Failed?.Error?.Message);
        await catalog.RemoveHandlerAsync(new RemoveHandlerRequest
        {
            Database = "school", HandlerName = "DefineCourse"
        });

        ExecuteHandlerResponse duplicate = await commands.ExecuteHandlerAsync(new ExecuteHandlerRequest
        {
            Database = "school",
            HandlerName = "missing-handler",
            CommandId = commandId.ToString("D"),
            CommandJson = ByteString.CopyFromUtf8("{")
        });

        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.AlreadyCommitted, duplicate.OutcomeCase);
        Assert.Equal("DefineCourse", duplicate.CommandType);
        Assert.Equal(expected: 1, duplicate.AlreadyCommitted.FirstEventId);
    }

    [Fact]
    public async Task Closed_partitions_eventually_receive_valid_state_files()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        AdministrationService.AdministrationServiceClient administration = new(_channel);

        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        await RegisterAsync(catalog, "SubscribeStudent", "SubscribeStudentToCourse", SubscribeNdl);
        await ExecuteAsync(commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 2 });
        await ExecuteAsync(
            commands, "SubscribeStudent", Guid.NewGuid(), new { StudentId = "student-1", CourseId = "course-1" });
        await ExecuteAsync(
            commands, "SubscribeStudent", Guid.NewGuid(), new { StudentId = "student-2", CourseId = "course-1" });

        ListPartitionsResponse? partitions = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            partitions = await administration.ListPartitionsAsync(
                new ListPartitionsRequest { Database = "school" });
            if (partitions.Partitions.Count == 2 && partitions.Partitions[index: 0].StateFile.SchemaValid)
            {
                break;
            }

            await Task.Delay(millisecondsDelay: 20);
        }

        Assert.NotNull(partitions);
        Assert.Equal(expected: 2, partitions.Partitions.Count);
        Assert.False(partitions.Partitions[index: 0].Active);
        Assert.True(partitions.Partitions[index: 0].StateFile.Present);
        Assert.True(partitions.Partitions[index: 0].StateFile.JsonValid);
        Assert.True(partitions.Partitions[index: 0].StateFile.SchemaValid);
        Assert.True(partitions.Partitions[index: 1].Active);

        RebuildResponse rebuild = await administration.RequestStateRebuildAsync(new RequestStateRebuildRequest
        {
            Database = "school", PartitionNumber = 1
        });
        Assert.True(rebuild.Accepted);
    }

    [Fact]
    public async Task Discovered_database_reports_writer_lock_failure_as_faulted_and_unavailable()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        await ExecuteAsync(commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 1 });

        _channel.Dispose();
        _factory.PreserveDatabaseRoot = true;
        await _factory.DisposeAsync();
        string lockPath = Path.Combine(_factory.DatabaseRoot, "school", "store.lock");
        await using FileStream competingWriter = new(
            lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        _factory = new ServerFactory(_factory.DatabaseRoot);
        _channel = GrpcChannel.ForAddress("http://localhost",
            new GrpcChannelOptions { HttpHandler = _factory.Server.CreateHandler() });
        databases = new DatabaseService.DatabaseServiceClient(_channel);

        ListDatabasesResponse discovered = await databases.ListDatabasesAsync(new ListDatabasesRequest());
        DatabaseSummary initial = Assert.Single(discovered.Databases);
        Assert.Equal(DatabaseState.Discovered, initial.State);
        Assert.True(initial.ReadAvailable);
        Assert.False(initial.WriteAvailable);

        EventService.EventServiceClient events = new(_channel);
        IReadOnlyList<EventEnvelope> readable = await ReadAllAsync(events.ReadEventsByRange(
            new ReadEventsByRangeRequest { Database = "school", AfterEventId = 0 }).ResponseStream);
        Assert.Equal(expected: 1, Assert.Single(readable).EventId);

        discovered = await databases.ListDatabasesAsync(new ListDatabasesRequest());
        Assert.Equal(DatabaseState.Discovered, Assert.Single(discovered.Databases).State);

        RpcException unavailable = await Assert.ThrowsAsync<RpcException>(async () =>
            await databases.GetHeadAsync(new GetHeadRequest { Database = "school" }));
        Assert.Equal(StatusCode.Unavailable, unavailable.StatusCode);

        ListDatabasesResponse faulted = await databases.ListDatabasesAsync(new ListDatabasesRequest());
        DatabaseSummary failed = Assert.Single(faulted.Databases);
        Assert.Equal(DatabaseState.Faulted, failed.State);
        Assert.True(failed.ReadAvailable);
        Assert.False(failed.WriteAvailable);
        Assert.NotNull(failed.Fault);
    }

    [Fact]
    public async Task Follow_range_replays_then_tails_committed_events()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        EventService.EventServiceClient events = new(_channel);

        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        using AsyncServerStreamingCall<EventEnvelope> follow = events.ReadEventsByRange(new ReadEventsByRangeRequest
        {
            Database = "school", AfterEventId = 0, Mode = ReadMode.Follow, Limit = 1
        });
        using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(seconds: 10));
        Task<bool> moved = follow.ResponseStream.MoveNext(timeout.Token);

        await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 1 });

        Assert.True(await moved);
        Assert.Equal(expected: 1, follow.ResponseStream.Current.EventId);
        Assert.Equal("CourseDefined", follow.ResponseStream.Current.Type);
        Assert.False(await follow.ResponseStream.MoveNext(timeout.Token));
    }

    [Fact]
    public async Task Committed_events_build_persistent_index_actor_state()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        AdministrationService.AdministrationServiceClient administration = new(_channel);
        EventService.EventServiceClient events = new(_channel);

        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 1 });

        ListIndexesResponse? indexes = null;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            indexes = await administration.ListIndexesAsync(new ListIndexesRequest { Database = "school" });
            if (indexes.Indexes.Count == 2 &&
                indexes.Indexes.All(value => value.HydrationState == HydrationState.Ready))
            {
                break;
            }

            await Task.Delay(millisecondsDelay: 20);
        }

        Assert.NotNull(indexes);
        Assert.Equal(expected: 2, indexes.Indexes.Count);
        Assert.All(indexes.Indexes, index =>
        {
            Assert.Equal(expected: 1, index.IndexHead);
            Assert.Equal(expected: 1, index.MainHead);
            Assert.Equal((ulong)0, index.Lag);
            Assert.True(File.Exists(Path.Combine(_factory.DatabaseRoot, "school", index.Filename)));
        });

        ReadEventsByQueryRequest eventualRequest = new()
        {
            Database = "school", Consistency = QueryConsistency.EventualIndex, Query = new Query()
        };
        eventualRequest.Query.Items.Add(new QueryItem
        {
            EventTypes = { "CourseDefined" }, Keys = { new KeyValue { Key = "CourseId", Value = "course-1" } }
        });
        IReadOnlyList<EventEnvelope> indexedEvents = await ReadAllAsync(
            events.ReadEventsByQuery(eventualRequest).ResponseStream);
        Assert.Equal(expected: 1, Assert.Single(indexedEvents).EventId);

        RebuildResponse rebuild = await administration.RequestIndexRebuildAsync(new RequestIndexRebuildRequest
        {
            Database = "school",
            EventType = "CourseDefined",
            Keys = { new KeyValue { Key = "CourseId", Value = "course-1" } }
        });
        Assert.True(rebuild.Accepted);
    }

    [Fact]
    public async Task Registered_schemas_validate_commands_and_drive_event_consistency_keys()
    {
        const string commandSchema = """
                                     {
                                       "type": "object",
                                       "properties": {
                                         "CourseId": { "type": "string" },
                                         "Capacity": { "type": "integer" }
                                       },
                                       "required": ["CourseId", "Capacity"],
                                       "additionalProperties": false
                                     }
                                     """;
        const string eventSchema = """
                                   {
                                     "type": "object",
                                     "properties": {
                                       "CourseId": {
                                         "type": "string",
                                         "x-native-dcb-consistency-key": "course"
                                       },
                                       "Capacity": { "type": "integer" }
                                     },
                                     "required": ["CourseId", "Capacity"],
                                     "additionalProperties": false
                                   }
                                   """;

        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await catalog.RegisterCommandSchemaAsync(new RegisterSchemaRequest
        {
            Database = "school",
            SchemaName = "DefineCourse",
            SchemaDocumentJson = ByteString.CopyFromUtf8(commandSchema)
        });
        await catalog.RegisterEventSchemaAsync(new RegisterSchemaRequest
        {
            Database = "school",
            SchemaName = "CourseDefined",
            SchemaDocumentJson = ByteString.CopyFromUtf8(eventSchema)
        });
        ListSchemasResponse schemas = await catalog.ListSchemasAsync(new ListSchemasRequest { Database = "school" });
        Assert.Equal(
            [(SchemaKind.Event, "CourseDefined"), (SchemaKind.Command, "DefineCourse")],
            schemas.Schemas.Select(value => (value.SchemaKind, value.SchemaName)));
        Assert.All(schemas.Schemas, value => Assert.NotEmpty(value.Fingerprint));

        ListSchemasResponse commandSchemas = await catalog.ListSchemasAsync(new ListSchemasRequest
        {
            Database = "school", SchemaKind = SchemaKind.Command
        });
        Assert.Equal("DefineCourse", Assert.Single(commandSchemas.Schemas).SchemaName);

        GetSchemaResponse inspectedSchema = await catalog.GetSchemaAsync(new GetSchemaRequest
        {
            Database = "school", SchemaName = "CourseDefined", SchemaKind = SchemaKind.Event
        });
        Assert.Equal(eventSchema, inspectedSchema.Schema.SchemaDocumentJson.ToStringUtf8());
        Assert.Equal(schemas.Schemas[index: 0].Fingerprint, inspectedSchema.Schema.Fingerprint);
        RpcException missingSchema = await Assert.ThrowsAsync<RpcException>(async () =>
            await catalog.GetSchemaAsync(new GetSchemaRequest
            {
                Database = "school", SchemaName = "Missing", SchemaKind = SchemaKind.Event
            }));
        Assert.Equal(StatusCode.NotFound, missingSchema.StatusCode);

        RegisterHandlerResponse handler = await RegisterAsync(
            catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        Assert.True(handler.Handler.Valid);

        ExecuteHandlerResponse invalid = await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-invalid" });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Failed, invalid.OutcomeCase);
        Assert.Equal("InvalidCommand", invalid.Failed.Error.Code);

        ExecuteHandlerResponse committed = await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 2 });
        Assert.True(
            committed.OutcomeCase == ExecuteHandlerResponse.OutcomeOneofCase.Committed,
            committed.Failed?.Error?.Message);
        EventEnvelope persisted = Assert.Single(committed.Committed.Events);
        KeyValue key = Assert.Single(persisted.Keys);
        Assert.Equal("course", key.Key);
        Assert.Equal("course-1", key.Value);

        string incompatibleSchema = eventSchema.Replace(
            "x-native-dcb-consistency-key\": \"course\"",
            "x-native-dcb-consistency-key\": \"course-v2\"",
            StringComparison.Ordinal);
        RegisterSchemaResponse incompatible = await catalog.RegisterEventSchemaAsync(new RegisterSchemaRequest
        {
            Database = "school",
            SchemaName = "CourseDefined",
            SchemaDocumentJson = ByteString.CopyFromUtf8(incompatibleSchema)
        });
        Assert.Contains(incompatible.Diagnostics, value => value.Code == "SCHEMA2001");
    }

    [Fact]
    public async Task Execute_statement_registers_compiled_decision_handlers()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        StatementService.StatementServiceClient statements = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });

        using AsyncServerStreamingCall<StatementResult> call = statements.ExecuteStatement(
            new ExecuteStatementRequest { Database = "school", NdlSource = CreateCourseNdl });
        List<StatementResult> results = new();
        while (await call.ResponseStream.MoveNext(CancellationToken.None))
        {
            results.Add(call.ResponseStream.Current);
        }

        RegistrationResult registration = Assert.Single(
            results, value => value.ResultCase == StatementResult.ResultOneofCase.Registration).Registration;
        Assert.Equal("handler", registration.Kind);
        Assert.Equal("DefineCourse", registration.Name);
        StatementCompletion completion = Assert.Single(
            results, value => value.ResultCase == StatementResult.ResultOneofCase.Completion).Completion;
        Assert.True(completion.Succeeded);

        ExecuteHandlerResponse executed = await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 1 });
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, executed.OutcomeCase);
    }

    [Fact]
    public async Task Fluent_sdk_plan_registers_and_executes_without_ndl_translation()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        EventService.EventServiceClient events = new(_channel);
        using NativeDcbClient sdk = new(commands, events, catalog);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await sdk.RegisterEventSchemaAsync<CourseDefinedSdk>("school");
        await sdk.RegisterEventSchemaAsync<StudentSubscribedSdk>("school");
        await sdk.RegisterCommandSchemaAsync<SubscribeSdk>("school");
        await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-1", Capacity = 2 });

        SubscribeSdk command = new("student-1", "course-1");
        DecisionDefinition<SubscribeSdk> definition = Decision.WithDecisionModel(command)
            .Include<CourseDefinedSdk, CourseModelSdk>((_, @event) => new CourseModelSdk(true, @event.Capacity))
            .Where(@event => @event.CourseId == command.CourseId)
            .Evaluate((model, _) => model.Exists)
            .Decide((accepted, input) => accepted
                ? Decision.Accept(new StudentSubscribedSdk(input.StudentId, input.CourseId))
                : Decision.Reject("Course does not exist"));
        RegisterHandlerResponse registration = await sdk.RegisterDecisionAsync(
            "school", "SubscribeStudentSdk", definition);
        Assert.True(registration.Handler.Valid);
        Assert.Empty(registration.Handler.NdlSource);

        GetHandlerResponse inspected = await sdk.GetHandlerAsync(
            "school", "SubscribeStudentSdk", includePlanJson: true, generateNdl: true);
        Assert.NotEmpty(inspected.Handler.PlanJson);
        using (JsonDocument plan = JsonDocument.Parse(inspected.Handler.PlanJson.Memory))
        {
            Assert.Equal("SubscribeStudentSdk", plan.RootElement.GetProperty("name").GetString());
        }

        Assert.StartsWith("decision SubscribeStudentSdk", inspected.Handler.GeneratedNdl, StringComparison.Ordinal);
        Assert.Empty(inspected.Handler.NdlGenerationDiagnostics);

        DecisionPlan unrepresentable = definition.Compile("Unrepresentable") with
        {
            CommandSchema = "subscribe-command"
        };
        RegisterHandlerResponse directRegistration = await catalog.RegisterHandlerAsync(new RegisterHandlerRequest
        {
            Database = "school",
            HandlerName = "Unrepresentable",
            CommandType = unrepresentable.CommandSchema,
            PlanJson = ByteString.CopyFrom(JsonSerializer.SerializeToUtf8Bytes(
                unrepresentable, new JsonSerializerOptions(JsonSerializerDefaults.Web)))
        });
        Assert.True(directRegistration.Handler.Valid);
        GetHandlerResponse failedGeneration = await catalog.GetHandlerAsync(new GetHandlerRequest
        {
            Database = "school", HandlerName = "Unrepresentable", GenerateNdl = true
        });
        Assert.Empty(failedGeneration.Handler.GeneratedNdl);
        Assert.Equal("NDL3001", Assert.Single(failedGeneration.Handler.NdlGenerationDiagnostics).Code);

        ExecuteHandlerResponse result = await sdk.ExecuteHandlerAsync(
            "school", "SubscribeStudentSdk", command, Guid.NewGuid());
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, result.OutcomeCase);
        EventEnvelope persisted = Assert.Single(result.Committed.Events);
        Assert.Equal("StudentSubscribedToCourse", persisted.Type);
        Assert.Equal(["student", "course"], persisted.Keys.Select(value => value.Key));
    }

    [Fact]
    public async Task Full_sdk_client_executes_database_statement_decision_and_administration_flow()
    {
        using NativeDcbClient client = new(
            new DatabaseService.DatabaseServiceClient(_channel),
            new CatalogService.CatalogServiceClient(_channel),
            new CommandService.CommandServiceClient(_channel),
            new EventService.EventServiceClient(_channel),
            new StatementService.StatementServiceClient(_channel),
            new AdministrationService.AdministrationServiceClient(_channel));

        await client.CreateDatabaseAsync("school");
        await client.RegisterEventSchemaAsync<CourseDefinedSdk>("school");
        await client.RegisterEventSchemaAsync<StudentSubscribedSdk>("school");
        await client.RegisterCommandSchemaAsync<DefineCourseSdk>("school");
        await client.RegisterCommandSchemaAsync<SubscribeSdk>("school");

        List<StatementResult> statementResults = new();
        await foreach (StatementResult result in client.ExecuteStatementAsync(
                           "school", CreateCourseNdl + Environment.NewLine + SchemaCompatibleSubscribeNdl))
        {
            statementResults.Add(result);
        }

        Assert.Equal(expected: 2, statementResults.Count(value =>
            value.ResultCase == StatementResult.ResultOneofCase.Registration));
        Assert.Equal(StatementResult.ResultOneofCase.Completion, statementResults[^1].ResultCase);

        SubscribeSdk template = new("template", "template");
        DecisionDefinition<SubscribeSdk> definition = Decision.WithDecisionModel(template)
            .Include<CourseDefinedSdk, CourseModelSdk>((_, @event) => new CourseModelSdk(true, @event.Capacity))
            .Where(@event => @event.CourseId == template.CourseId)
            .Evaluate((model, _) => model.Exists)
            .Decide((accepted, command) => accepted
                ? Decision.Accept(new StudentSubscribedSdk(command.StudentId, command.CourseId))
                : Decision.Reject("Course does not exist"));
        await client.RegisterDecisionAsync("school", "SubscribeStudentSdk", definition);

        ExecuteHandlerResponse defined = await client.ExecuteHandlerAsync(
            "school", "DefineCourse", new DefineCourseSdk("course-1", Capacity: 10));
        ExecuteHandlerResponse ndl = await client.ExecuteHandlerAsync(
            "school", "SubscribeStudent", new SubscribeSdk("student-ndl", "course-1"));
        ExecuteHandlerResponse sdk = await client.ExecuteHandlerAsync(
            "school", "SubscribeStudentSdk", new SubscribeSdk("student-sdk", "course-1"));

        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, defined.OutcomeCase);
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, ndl.OutcomeCase);
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, sdk.OutcomeCase);
        Assert.Equal(expected: 3, (await client.GetHeadAsync("school")).EventId);
        Assert.NotEmpty((await client.ListPartitionsAsync("school")).Partitions);
    }

    private static async Task<RegisterHandlerResponse> RegisterAsync(
        CatalogService.CatalogServiceClient client,
        string name,
        string commandType,
        string source)
    {
        return await client.RegisterHandlerAsync(new RegisterHandlerRequest
        {
            Database = "school", HandlerName = name, CommandType = commandType, NdlSource = source
        });
    }

    private static async Task<ExecuteHandlerResponse> ExecuteAsync(
        CommandService.CommandServiceClient client,
        string handler,
        Guid commandId,
        object command)
    {
        return await client.ExecuteHandlerAsync(new ExecuteHandlerRequest
        {
            Database = "school",
            HandlerName = handler,
            CommandId = commandId.ToString("D"),
            CommandJson = ByteString.CopyFromUtf8(JsonSerializer.Serialize(command))
        });
    }

    private static async Task<IReadOnlyList<EventEnvelope>> ReadAllAsync(IAsyncStreamReader<EventEnvelope> stream)
    {
        List<EventEnvelope> results = new();
        while (await stream.MoveNext(CancellationToken.None))
        {
            results.Add(stream.Current);
        }

        return results;
    }

    private static void AssertErrorDetail(RpcException exception, string expectedCode)
    {
        Metadata.Entry entry = Assert.Single(
            exception.Trailers,
            value => value.Key == "native-dcb-error-bin");
        ErrorDetail detail = ErrorDetail.Parser.ParseFrom(entry.ValueBytes);
        Assert.Equal(expectedCode, detail.Code);
        Assert.Equal(exception.Status.Detail, detail.Message);
        Assert.Equal("{}", detail.DetailsJson.ToStringUtf8());
    }

    private sealed class ServerFactory(string? databaseRoot = null) : WebApplicationFactory<Program>
    {
        public string DatabaseRoot { get; } = databaseRoot ?? Path.Combine(
            Path.GetTempPath(), "NativeDCB.Server.Tests", Guid.NewGuid().ToString("N"));

        public bool PreserveDatabaseRoot { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DatabaseRoot"] = DatabaseRoot, ["MaxEventCountPerPartition"] = "3"
                }));
        }

        public override async ValueTask DisposeAsync()
        {
            await base.DisposeAsync();
            if (Directory.Exists(DatabaseRoot) && !PreserveDatabaseRoot)
            {
                Directory.Delete(DatabaseRoot, recursive: true);
            }
        }
    }

    [EventType("CourseDefined")]
    // ReSharper disable once ClassNeverInstantiated.Local -- The SDK reflects over this event contract.
    private sealed record CourseDefinedSdk(
        [property: ConsistencyKey("course")] string CourseId,
        int Capacity);

    [EventType("StudentSubscribedToCourse")]
    private sealed record StudentSubscribedSdk(
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Used by SDK serialization and schema reflection.
        [property: ConsistencyKey("student")] string StudentId,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Used by SDK serialization and schema reflection.
        [property: ConsistencyKey("course")] string CourseId);

    [CommandType("SubscribeStudentToCourse")]
    private sealed record SubscribeSdk(string StudentId, string CourseId);

    [CommandType("DefineCourse")]
    private sealed record DefineCourseSdk(
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Used by SDK serialization and schema reflection.
        string CourseId,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Used by SDK serialization and schema reflection.
        int Capacity);

    private sealed record CourseModelSdk(
        bool Exists,
        // ReSharper disable once NotAccessedPositionalProperty.Local -- Read by the generated decision plan.
        int Capacity);
}