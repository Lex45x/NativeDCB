using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Text.Json;

using Google.Protobuf;

using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

using NativeDCB.Model.Decisions;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Schemas;
using NativeDCB.Server.Security;

using QueryItem = NativeDCB.Protocol.V1.QueryItem;

// Test-only contracts are consumed through JSON serialization and schema reflection.
// ReSharper disable ClassNeverInstantiated.Local
// ReSharper disable NotAccessedPositionalProperty.Local
namespace NativeDCB.Server.IntegrationTests.Grpc;

public sealed partial class NativeDcbServerTests : IAsyncLifetime
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

    private const string SchemaCompatibleSubscribeNdl =
        """
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

    private const string RemoteBatchNdl =
        """
        decision RemoteBatch
        from SubscribeStudentToCourse command
        | include CourseDefined event
            where event.CourseId == command.CourseId
            apply {
                CourseExists = true,
                CourseCapacity = event.Capacity
            }
        | evaluate {
            require (model.CourseExists ?? false) else "Course does not exist";
        }
        | decide {
            emit CourseDefined {
                CourseId = command.CourseId,
                Capacity = model.CourseCapacity
            };
            emit StudentSubscribedToCourse {
                StudentId = command.StudentId,
                CourseId = command.CourseId,
                RemainingSeats = model.CourseCapacity - 1
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
        Assert.True(File.Exists(Path.Combine(_factory.DatabaseRoot, "school", "schemas_v1.json")));
        Assert.True(File.Exists(Path.Combine(_factory.DatabaseRoot, "school", "handlers_v1.json")));

        RegisterHandlerResponse define = await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        RegisterHandlerResponse subscribe = await RegisterAsync(
            catalog, "SubscribeStudent", "SubscribeStudentToCourse", SubscribeNdl);
        Assert.True(define.Handler.Valid);
        Assert.True(subscribe.Handler.Valid);

        ExplainStatementResponse explained = await statements.ExplainStatementAsync(new ExplainStatementRequest
        {
            Database = "school",
            NdlSource = SubscribeNdl
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
            Database = "school",
            HandlerName = "SubscribeStudent"
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
            Database = "school",
            HandlerName = "SubscribeStudent",
            IncludePlanJson = true,
            GenerateNdl = true
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
                Database = "empty",
                CommandId = Guid.NewGuid().ToString("D")
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
            Database = "school",
            HandlerName = "DefineCourse"
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

        GetStateFileStatusResponse stateStatus = await administration.GetStateFileStatusAsync(
            new GetStateFileStatusRequest { Database = "school", PartitionNumber = 1 });
        Assert.True(stateStatus.Status.Present);
        Assert.True(stateStatus.Status.JsonValid);
        Assert.True(stateStatus.Status.SchemaValid);

        RebuildResponse rebuild = await administration.RequestStateRebuildAsync(new RequestStateRebuildRequest
        {
            Database = "school",
            PartitionNumber = 1
        });
        Assert.True(rebuild.Accepted);

        string[] outcomes = [];
        for (int attempt = 0; attempt < 50; attempt++)
        {
            outcomes = await AuditOutcomesAsync(
                "/nativedcb.v1.AdministrationService/RequestStateRebuild");
            if (outcomes.Contains("completed", StringComparer.Ordinal))
            {
                break;
            }

            await Task.Delay(millisecondsDelay: 20);
        }

        Assert.Equal(["dispatched", "completed"], outcomes);
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
            Database = "school",
            AfterEventId = 0,
            Mode = ReadMode.Follow,
            Limit = 1
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
        ListIndexesResponse emptyIndexes = await administration.ListIndexesAsync(
            new ListIndexesRequest { Database = "school" });
        Assert.Empty(emptyIndexes.Indexes);

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
            string manifestPath = Path.Combine(_factory.DatabaseRoot, "school", index.Filename);
            Assert.True(File.Exists(manifestPath));
            using JsonDocument manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
            string generationFile = manifest.RootElement.GetProperty("generationFile").GetString()!;
            Assert.Matches("^generation_00000000000000000001_[0-9a-f]{32}_v1\\.json$", generationFile);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(manifestPath)!, generationFile)));
        });

        ReadEventsByQueryRequest eventualRequest = new()
        {
            Database = "school",
            Consistency = QueryConsistency.EventualIndex,
            Query = new Query()
        };
        eventualRequest.Query.Items.Add(new QueryItem
        {
            EventTypes = { "CourseDefined" },
            Keys = { new KeyValue { Key = "CourseId", Value = "course-1" } }
        });
        IReadOnlyList<EventEnvelope> indexedEvents = await ReadAllAsync(
            events.ReadEventsByQuery(eventualRequest).ResponseStream);
        Assert.Equal(expected: 1, Assert.Single(indexedEvents).EventId);

        IndexStatus capacityIndex = Assert.Single(
            indexes.Indexes, value => value.Keys.Single().Key == "Capacity");
        string capacityManifestPath = Path.Combine(
            _factory.DatabaseRoot, "school", capacityIndex.Filename);
        string staleManifest = File.ReadAllText(capacityManifestPath);
        await ExecuteAsync(
            commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "course-2", Capacity = 1 });
        long publishedHead = 0;
        for (int attempt = 0; attempt < 50; attempt++)
        {
            try
            {
                using JsonDocument published = JsonDocument.Parse(File.ReadAllText(capacityManifestPath));
                publishedHead = published.RootElement.GetProperty("head").GetInt64();
                if (publishedHead == 2)
                {
                    break;
                }
            }
            catch (IOException)
            {
                // Atomic replacement can briefly deny a concurrent open on Windows.
            }

            await Task.Delay(millisecondsDelay: 20);
        }

        Assert.Equal(expected: 2, publishedHead);
        File.WriteAllText(capacityManifestPath, staleManifest);

        ReadEventsByQueryRequest capacityRequest = new()
        {
            Database = "school",
            Consistency = QueryConsistency.EventualIndex,
            Query = new Query()
        };
        capacityRequest.Query.Items.Add(new QueryItem
        {
            EventTypes = { "CourseDefined" },
            Keys = { new KeyValue { Key = "Capacity", Value = "1" } }
        });
        IReadOnlyList<EventEnvelope> staleEvents = await ReadAllAsync(
            events.ReadEventsByQuery(capacityRequest).ResponseStream);
        Assert.Equal(expected: 1, Assert.Single(staleEvents).EventId);

        capacityRequest.Consistency = QueryConsistency.CommittedScan;
        IReadOnlyList<EventEnvelope> committedEvents = await ReadAllAsync(
            events.ReadEventsByQuery(capacityRequest).ResponseStream);
        Assert.Equal([1L, 2L], committedEvents.Select(value => value.EventId));

        using (JsonDocument manifest = JsonDocument.Parse(staleManifest))
        {
            string generationFile = manifest.RootElement.GetProperty("generationFile").GetString()!;
            File.WriteAllText(Path.Combine(Path.GetDirectoryName(capacityManifestPath)!, generationFile), "{}");
        }

        capacityRequest.Consistency = QueryConsistency.EventualIndex;
        IReadOnlyList<EventEnvelope> recoveredEvents = await ReadAllAsync(
            events.ReadEventsByQuery(capacityRequest).ResponseStream);
        Assert.Equal([1L, 2L], recoveredEvents.Select(value => value.EventId));

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
            Database = "school",
            SchemaKind = SchemaKind.Command
        });
        Assert.Equal("DefineCourse", Assert.Single(commandSchemas.Schemas).SchemaName);

        GetSchemaResponse inspectedSchema = await catalog.GetSchemaAsync(new GetSchemaRequest
        {
            Database = "school",
            SchemaName = "CourseDefined",
            SchemaKind = SchemaKind.Event
        });
        Assert.Equal(eventSchema, inspectedSchema.Schema.SchemaDocumentJson.ToStringUtf8());
        Assert.Equal(schemas.Schemas[index: 0].Fingerprint, inspectedSchema.Schema.Fingerprint);
        RpcException missingSchema = await Assert.ThrowsAsync<RpcException>(async () =>
            await catalog.GetSchemaAsync(new GetSchemaRequest
            {
                Database = "school",
                SchemaName = "Missing",
                SchemaKind = SchemaKind.Event
            }));
        Assert.Equal(StatusCode.NotFound, missingSchema.StatusCode);

        RegisterHandlerResponse handler = await RegisterAsync(
            catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        Assert.True(handler.Handler.Valid);
        RegisterHandlerResponse unchangedHandler = await RegisterAsync(
            catalog, "DefineCourse", "DefineCourse", CreateCourseNdl);
        Assert.Equal(handler.Handler.PlanFingerprint, unchangedHandler.Handler.PlanFingerprint);

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

        AuditService.AuditServiceClient audit = new(_channel);
        ListAuditRecordsResponse handlerAudit = await audit.ListAuditRecordsAsync(new ListAuditRecordsRequest
        {
            Database = "school",
            Operation = "/nativedcb.v1.CatalogService/RegisterHandler",
            Phase = "outcome",
            Limit = 100
        });
        Assert.Equal(["created", "unchanged"], handlerAudit.Records.Select(value => value.Outcome));

        ListAuditRecordsResponse schemaAudit = await audit.ListAuditRecordsAsync(new ListAuditRecordsRequest
        {
            Database = "school",
            Operation = "/nativedcb.v1.CatalogService/RegisterEventSchema",
            Phase = "outcome",
            Limit = 100
        });
        Assert.Contains(schemaAudit.Records, value => value.Outcome == "incompatible");
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
            Database = "school",
            HandlerName = "Unrepresentable",
            GenerateNdl = true
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
        ConcurrentQueue<string> activities = new();
        using ActivityListener activityListener = new()
        {
            ShouldListenTo = source => source.Name == "NativeDCB.Actors",
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
            ActivityStopped = activity => activities.Enqueue(activity.OperationName)
        };
        ActivitySource.AddActivityListener(activityListener);

        ConcurrentQueue<(string Name, string[] Tags)> measurements = new();
        using MeterListener meterListener = new();
        meterListener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == "NativeDCB.Actors")
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, tags.ToArray().Select(tag => tag.Key).ToArray())));
        meterListener.Start();

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

        AuditService.AuditServiceClient audit = new(_channel);
        ListAuditRecordsResponse decisionAudit = await audit.ListAuditRecordsAsync(new ListAuditRecordsRequest
        {
            Database = "school",
            Operation = "/nativedcb.v1.CommandService/ExecuteHandler",
            Limit = 100
        });
        Assert.Equal(expected: 3, decisionAudit.Records.Count(record => record.Phase == "attempt"));
        Assert.Equal(expected: 3, decisionAudit.Records.Count(record =>
            record.Phase == "outcome" && record.Outcome == "committed"));
        Assert.All(decisionAudit.Records.Where(record => record.Phase == "outcome"), record =>
        {
            Assert.True(record.HasCommandId);
            Assert.True(record.HasFirstEventId);
            Assert.True(record.HasLastEventId);
        });
        Assert.Contains("decision.execute", activities);
        Assert.Contains("query", activities);
        Assert.Contains(measurements, value => value.Name == "nativedcb.decisions");
        Assert.Contains(measurements, value => value.Name == "nativedcb.queries");
        Assert.Contains(measurements, value => value.Name == "nativedcb.database.transitions");
        Assert.DoesNotContain(measurements.SelectMany(value => value.Tags), tag =>
            tag is "database" or "handler" or "subject" or "command_id" or "event_type");
    }

    [Fact]
    public async Task Audited_transport_validation_records_invalid_outcomes()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        AdministrationService.AdministrationServiceClient administration = new(_channel);
        AuditService.AuditServiceClient audit = new(_channel);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });

        RpcException invalidCommand = await Assert.ThrowsAsync<RpcException>(async () =>
            await commands.ExecuteHandlerAsync(new ExecuteHandlerRequest
            {
                Database = "school",
                HandlerName = "missing",
                CommandId = "not-a-uuid"
            }));
        Assert.Equal(StatusCode.InvalidArgument, invalidCommand.StatusCode);

        RpcException invalidSchema = await Assert.ThrowsAsync<RpcException>(async () =>
            await catalog.RemoveSchemaAsync(new RemoveSchemaRequest
            {
                Database = "school",
                SchemaName = "missing"
            }));
        Assert.Equal(StatusCode.InvalidArgument, invalidSchema.StatusCode);

        RpcException invalidRebuild = await Assert.ThrowsAsync<RpcException>(async () =>
            await administration.RequestIndexRebuildAsync(new RequestIndexRebuildRequest
            {
                Database = "school"
            }));
        Assert.Equal(StatusCode.InvalidArgument, invalidRebuild.StatusCode);

        foreach (string operation in new[]
                 {
                     "/nativedcb.v1.CommandService/ExecuteHandler",
                     "/nativedcb.v1.CatalogService/RemoveSchema",
                     "/nativedcb.v1.AdministrationService/RequestIndexRebuild"
                 })
        {
            ListAuditRecordsResponse records = await audit.ListAuditRecordsAsync(new ListAuditRecordsRequest
            {
                Database = "school",
                Operation = operation,
                Limit = 100
            });
            Assert.Collection(records.Records,
                attempt => Assert.Equal("attempt", attempt.Phase),
                outcome =>
                {
                    Assert.Equal("outcome", outcome.Phase);
                    Assert.Equal("invalid", outcome.Outcome);
                });
            Assert.Equal(records.Records[0].OperationId, records.Records[1].OperationId);
        }
    }

    [Fact]
    public async Task Remote_decision_prepares_model_completes_with_derived_keys_and_replays()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        Guid commandId = Guid.Parse("00000000-0000-0000-0000-000000000002");

        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school", "SubscribeStudent", new SubscribeSdk("student-1", "course-1"), commandId);

        Assert.Equal(new RemoteSubscribeModel(CourseExists: true, CourseCapacity: 2), prepared.Model);
        Assert.NotEmpty(prepared.ModelSignature);
        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Committed, completed.OutcomeCase);
        EventEnvelope persisted = Assert.Single(completed.Committed.Events);
        Assert.Equal(["student", "course"], persisted.Keys.Select(value => value.Key));
        Assert.Equal(["student-1", "course-1"], persisted.Keys.Select(value => value.Value));

        CompleteDecisionResponse replayed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("ignored", "ignored", RemainingSeats: 0))
            ]);
        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.AlreadyCommitted, replayed.OutcomeCase);
        Assert.Equal(expected: 2, replayed.AlreadyCommitted.FirstEventId);
        Assert.Equal(["prepared"], await AuditOutcomesAsync(
            "/nativedcb.v1.CommandService/PrepareDecision"));
        Assert.Equal(["committed", "already_committed"], await AuditOutcomesAsync(
            "/nativedcb.v1.CommandService/CompleteDecision"));
    }

    [Fact]
    public async Task Remote_decision_rejects_tampered_signature()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        byte[] tampered = prepared.ModelSignature.ToByteArray();
        tampered[^1] ^= 0x01;

        RpcException exception = await Assert.ThrowsAsync<RpcException>(() => client.CompleteDecisionAsync(
            "school",
            ByteString.CopyFrom(tampered),
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
            ]));

        Assert.Equal(StatusCode.InvalidArgument, exception.StatusCode);
    }

    [Fact]
    public async Task Remote_decision_requires_the_plan_emission_order_and_count()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));

        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1)),
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-2", "course-1", RemainingSeats: 0))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Failed, completed.OutcomeCase);
        Assert.Equal("InvalidEvents", completed.Failed.Error.Code);
        Assert.Equal(expected: 1, (await new DatabaseService.DatabaseServiceClient(_channel)
            .GetHeadAsync(new GetHeadRequest { Database = "school" })).EventId);
        Assert.Contains("failed", await AuditOutcomesAsync(
            "/nativedcb.v1.CommandService/CompleteDecision"));
    }

    [Fact]
    public async Task Remote_decision_enforces_distinct_emission_order_and_accepts_the_exact_sequence()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        RegisterHandlerResponse registered = await RegisterAsync(
            new CatalogService.CatalogServiceClient(_channel),
            "RemoteBatch",
            "SubscribeStudentToCourse",
            RemoteBatchNdl);
        Assert.True(registered.Handler.Valid,
            string.Join(Environment.NewLine, registered.Handler.Diagnostics.Select(value => value.Message)));
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "RemoteBatch",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        ProposedDecisionEvent course = new("CourseDefined", new CourseDefinedSdk("course-1", Capacity: 2));
        ProposedDecisionEvent subscription = new(
            "StudentSubscribedToCourse",
            new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1));

        CompleteDecisionResponse reordered = await client.CompleteDecisionAsync(
            "school", prepared.ModelSignature, [subscription, course]);
        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school", prepared.ModelSignature, [course, subscription]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Failed, reordered.OutcomeCase);
        Assert.Equal("InvalidEvents", reordered.Failed.Error.Code);
        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Committed, completed.OutcomeCase);
        Assert.Equal(["CourseDefined", "StudentSubscribedToCourse"],
            completed.Committed.Events.Select(value => value.Type));
    }

    [Fact]
    public async Task Remote_decision_matching_intervening_event_is_stale_and_appends_nothing()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        ExecuteHandlerResponse intervening = await client.ExecuteHandlerAsync(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-2", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000003"));
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, intervening.OutcomeCase);

        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Stale, completed.OutcomeCase);
        Assert.Equal(expected: 2, completed.Stale.CurrentHead);
        IReadOnlyList<EventEnvelope> persisted = await ReadAllAsync(new EventService.EventServiceClient(_channel)
            .ReadEventsByRange(new ReadEventsByRangeRequest { Database = "school", AfterEventId = 0 }).ResponseStream);
        Assert.Equal(expected: 2, persisted.Count);
        Assert.Contains("stale", await AuditOutcomesAsync(
            "/nativedcb.v1.CommandService/CompleteDecision"));
    }

    [Fact]
    public async Task Remote_decision_unrelated_intervening_event_does_not_stale_model()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        ExecuteHandlerResponse intervening = await client.ExecuteHandlerAsync(
            "school",
            "DefineCourse",
            new DefineCourseSdk("course-2", Capacity: 1),
            Guid.Parse("00000000-0000-0000-0000-000000000003"));
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, intervening.OutcomeCase);

        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Committed, completed.OutcomeCase);
        Assert.Equal(expected: 3, completed.Committed.FirstEventId);
    }

    [Fact]
    public async Task Remote_decision_expired_at_the_injected_clock_appends_nothing()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        _factory.Clock.Advance(prepared.ExpiresUtc - _factory.Clock.GetUtcNow());

        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Expired, completed.OutcomeCase);
        Assert.Equal(prepared.ExpiresUtc, completed.Expired.ExpiresUtc.ToDateTimeOffset());
        Assert.Equal(expected: 1, (await new DatabaseService.DatabaseServiceClient(_channel)
            .GetHeadAsync(new GetHeadRequest { Database = "school" })).EventId);
        Assert.Contains("expired", await AuditOutcomesAsync(
            "/nativedcb.v1.CommandService/CompleteDecision"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Remote_decision_changed_handler_after_prepare_is_invalidated(bool replaceHandler)
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        CatalogService.CatalogServiceClient catalog = new(_channel);
        if (replaceHandler)
        {
            string replacement = SubscribeNdl.Replace(
                "RemainingSeats = max(remainingSeats, 0)",
                "RemainingSeats = max(remainingSeats, 1)",
                StringComparison.Ordinal);
            RegisterHandlerResponse registered = await RegisterAsync(
                catalog, "SubscribeStudent", "SubscribeStudentToCourse", replacement);
            Assert.True(registered.Handler.Valid);
            Assert.NotEqual(prepared.PlanFingerprint, registered.Handler.PlanFingerprint);
        }
        else
        {
            await catalog.RemoveHandlerAsync(new RemoveHandlerRequest
            {
                Database = "school",
                HandlerName = "SubscribeStudent"
            });
        }

        CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
            "school",
            prepared.ModelSignature,
            [
                new ProposedDecisionEvent(
                    "StudentSubscribedToCourse",
                    new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
            ]);

        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Invalidated, completed.OutcomeCase);
        Assert.Equal("HandlerChanged", completed.Invalidated.Code);
        Assert.Equal(expected: 1, (await new DatabaseService.DatabaseServiceClient(_channel)
            .GetHeadAsync(new GetHeadRequest { Database = "school" })).EventId);
        Assert.Contains("invalidated", await AuditOutcomesAsync(
            "/nativedcb.v1.CommandService/CompleteDecision"));
    }

    [Fact]
    public async Task Concurrent_remote_completion_commits_one_signature_once()
    {
        using NativeDcbClient client = await CreateRemoteDecisionClientAsync();
        PreparedDecision<RemoteSubscribeModel> prepared = await client.PrepareDecisionAsync<
            SubscribeSdk, RemoteSubscribeModel>(
            "school",
            "SubscribeStudent",
            new SubscribeSdk("student-1", "course-1"),
            Guid.Parse("00000000-0000-0000-0000-000000000002"));
        ProposedDecisionEvent[] proposed =
        [
            new(
                "StudentSubscribedToCourse",
                new RemoteStudentSubscribedSdk("student-1", "course-1", RemainingSeats: 1))
        ];

        CompleteDecisionResponse[] completed = await Task.WhenAll(
            client.CompleteDecisionAsync("school", prepared.ModelSignature, proposed),
            client.CompleteDecisionAsync("school", prepared.ModelSignature, proposed));

        CompleteDecisionResponse committed = Assert.Single(
            completed, value => value.OutcomeCase == CompleteDecisionResponse.OutcomeOneofCase.Committed);
        CompleteDecisionResponse replayed = Assert.Single(
            completed, value => value.OutcomeCase == CompleteDecisionResponse.OutcomeOneofCase.AlreadyCommitted);
        Assert.Equal(expected: 2, committed.Committed.FirstEventId);
        Assert.Equal(expected: 2, replayed.AlreadyCommitted.FirstEventId);
    }

    [Fact]
    public async Task Local_handler_allows_guarded_missing_model_field_after_replay()
    {
        const string replayNdl = """
                                 decision Replay
                                 from Replay command
                                 | include CourseDefined event
                                     where event.CourseId == command.CourseId
                                     apply { MissingValue = event.NotPresent }
                                 | evaluate {
                                     require not exists(model.MissingValue) else "Unexpected value";
                                     let safeValue = model.MissingValue ?? "fallback";
                                 }
                                 | decide {
                                     emit Replayed { CourseId = command.CourseId, Value = safeValue };
                                 };
                                 """;
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        Assert.True((await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl)).Handler.Valid);
        Assert.True((await RegisterAsync(catalog, "Replay", "Replay", replayNdl)).Handler.Valid);
        Assert.Equal(
            ExecuteHandlerResponse.OutcomeOneofCase.Committed,
            (await ExecuteAsync(
                commands, "DefineCourse", Guid.NewGuid(), new { CourseId = "one", Capacity = 1 })).OutcomeCase);

        ExecuteHandlerResponse replayed = await ExecuteAsync(
            commands, "Replay", Guid.NewGuid(), new { CourseId = "one" });

        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, replayed.OutcomeCase);
        using JsonDocument payload = JsonDocument.Parse(
            Assert.Single(replayed.Committed.Events).DataJson.ToByteArray());
        Assert.Equal("fallback", payload.RootElement.GetProperty("Value").GetString());
    }

    private async Task<NativeDcbClient> CreateRemoteDecisionClientAsync()
    {
        DatabaseService.DatabaseServiceClient databases = new(_channel);
        CatalogService.CatalogServiceClient catalog = new(_channel);
        CommandService.CommandServiceClient commands = new(_channel);
        NativeDcbClient client = new(commands, new EventService.EventServiceClient(_channel), catalog);
        await databases.CreateDatabaseAsync(new CreateDatabaseRequest { Database = "school" });
        await client.RegisterEventSchemaAsync<CourseDefinedSdk>("school");
        await client.RegisterEventSchemaAsync<RemoteStudentSubscribedSdk>("school");
        await client.RegisterCommandSchemaAsync<DefineCourseSdk>("school");
        await client.RegisterCommandSchemaAsync<SubscribeSdk>("school");
        Assert.True((await RegisterAsync(catalog, "DefineCourse", "DefineCourse", CreateCourseNdl)).Handler.Valid);
        Assert.True((await RegisterAsync(
            catalog, "SubscribeStudent", "SubscribeStudentToCourse", SubscribeNdl)).Handler.Valid);
        ExecuteHandlerResponse course = await client.ExecuteHandlerAsync(
            "school",
            "DefineCourse",
            new DefineCourseSdk("course-1", Capacity: 2),
            Guid.Parse("00000000-0000-0000-0000-000000000001"));
        Assert.Equal(ExecuteHandlerResponse.OutcomeOneofCase.Committed, course.OutcomeCase);
        return client;
    }

    private static async Task<RegisterHandlerResponse> RegisterAsync(
        CatalogService.CatalogServiceClient client,
        string name,
        string commandType,
        string source)
    {
        return await client.RegisterHandlerAsync(new RegisterHandlerRequest
        {
            Database = "school",
            HandlerName = name,
            CommandType = commandType,
            NdlSource = source
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

    private async Task<string[]> AuditOutcomesAsync(string operation)
    {
        ListAuditRecordsResponse records = await new AuditService.AuditServiceClient(_channel)
            .ListAuditRecordsAsync(new ListAuditRecordsRequest
            {
                Database = "school",
                Operation = operation,
                Phase = "outcome",
                Limit = 100
            });
        return records.Records.Select(value => value.Outcome).ToArray();
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

    private sealed class ServerFactory(
        string? databaseRoot = null,
        MutableTimeProvider? timeProvider = null,
        bool authenticationDisabled = true,
        bool jwtAuthentication = false) : WebApplicationFactory<Program>
    {
        public string DatabaseRoot { get; } = databaseRoot ?? Path.Combine(
            Path.GetTempPath(), "NativeDCB.Server.Tests", Guid.NewGuid().ToString("N"));

        public MutableTimeProvider Clock { get; } = timeProvider ?? new MutableTimeProvider(
            new DateTimeOffset(year: 2026, month: 1, day: 1, hour: 0, minute: 0, second: 0, TimeSpan.Zero));

        public bool PreserveDatabaseRoot { get; set; }

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Authentication:Providers"] = jwtAuthentication
                        ? "JwtBearer"
                        : authenticationDisabled
                            ? "Disabled"
                            : "ApiKey",
                    ["Authentication:JwtBearer:Authority"] = jwtAuthentication ? JwtIssuer : null,
                    ["Authentication:JwtBearer:Audience"] = jwtAuthentication ? JwtAudience : null,
                    ["DatabaseRoot"] = DatabaseRoot,
                    ["MaxEventCountPerPartition"] = "3",
                    ["RemoteDecisions:ActiveKeyId"] = "test",
                    ["RemoteDecisions:SigningKeys:test"] = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=",
                    ["RemoteDecisions:Lifetime"] = "00:10:00"
                }));
            builder.ConfigureTestServices(services =>
            {
                if (authenticationDisabled)
                {
                    services.RemoveAll<IValidateOptions<NativeDcbAuthenticationOptions>>();
                }

                if (jwtAuthentication)
                {
                    ConfigureTestJwt(services);
                }

                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(Clock);
            });
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

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow()
        {
            return _utcNow;
        }

        public void Advance(TimeSpan duration)
        {
            _utcNow = _utcNow.Add(duration);
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

    [EventType("StudentSubscribedToCourse")]
    private sealed record RemoteStudentSubscribedSdk(
        [property: ConsistencyKey("student")] string StudentId,
        [property: ConsistencyKey("course")] string CourseId,
        int RemainingSeats);

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

    private sealed record RemoteSubscribeModel(bool CourseExists, int CourseCapacity);
}