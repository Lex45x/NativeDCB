using System.Text.Json;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using Grpc.Core;

using NativeDCB.Model.Events;
using NativeDCB.Model.Queries;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk.Client;

using QueryItem = NativeDCB.Model.Queries.QueryItem;

// Test-only contracts are consumed through JSON serialization.
// ReSharper disable NotAccessedPositionalProperty.Local
namespace NativeDCB.Sdk.Tests.Client;

public sealed class NativeDcbClientTests
{
    [Fact]
    public void Execute_request_maps_command_to_utf8_json_and_optional_id()
    {
        Guid id = Guid.Parse("4b7b85f9-e897-4e25-93c1-9bc3da29fb9a");
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        ExecuteHandlerRequest request = NativeDcbClient.BuildExecuteHandlerRequest(
            "school",
            "subscribe-student",
            new TestCommand("student-1", Attempt: 3),
            id,
            options);

        Assert.Equal("school", request.Database);
        Assert.Equal("subscribe-student", request.HandlerName);
        Assert.True(request.HasCommandId);
        Assert.Equal(id.ToString("D"), request.CommandId);
        Assert.Equal("{\"studentId\":\"student-1\",\"attempt\":3}", request.CommandJson.ToStringUtf8());
    }

    [Fact]
    public void Execute_request_omits_command_id_when_not_supplied()
    {
        ExecuteHandlerRequest request = NativeDcbClient.BuildExecuteHandlerRequest("db", "handler", new { Value = 1 });

        Assert.False(request.HasCommandId);
    }

    [Fact]
    public void Prepare_request_maps_command_and_optional_id()
    {
        Guid commandId = Guid.Parse("83d4527d-e6f8-4dce-8799-eafc2d0b6400");
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

        PrepareDecisionRequest request = NativeDcbClient.BuildPrepareDecisionRequest(
            "school", "subscribe-student", new TestCommand("student-1", Attempt: 2), commandId, options);
        PrepareDecisionRequest requestWithoutId = NativeDcbClient.BuildPrepareDecisionRequest(
            "school", "subscribe-student", new TestCommand("student-2", Attempt: 1), jsonOptions: options);

        Assert.Equal("school", request.Database);
        Assert.Equal("subscribe-student", request.HandlerName);
        Assert.Equal(commandId.ToString("D"), request.CommandId);
        Assert.Equal("{\"studentId\":\"student-1\",\"attempt\":2}", request.CommandJson.ToStringUtf8());
        Assert.False(requestWithoutId.HasCommandId);
    }

    [Fact]
    public async Task Prepare_deserializes_typed_model_and_preserves_signature_and_response()
    {
        ByteString signature = ByteString.CopyFrom([0, 1, 127, 255]);
        DateTimeOffset expiry = DateTimeOffset.Parse("2026-08-18T12:30:00Z");
        PrepareDecisionResponse response = new()
        {
            CommandId = "83d4527d-e6f8-4dce-8799-eafc2d0b6400",
            CommandType = "subscribe",
            Prepared = new PreparedDecision
            {
                ModelJson = ByteString.CopyFromUtf8("{\"StudentId\":\"student-1\",\"RemainingSeats\":4}"),
                ModelSignature = signature,
                ExpiresUtc = Timestamp.FromDateTimeOffset(expiry),
                PlanFingerprint = "sha256:plan"
            }
        };
        UnaryCallInvoker invoker = new(_ => response);
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using NativeDcbClient client = CreateClient(invoker, options);

        PreparedDecision<TestModel> prepared = await client.PrepareDecisionAsync<TestCommand, TestModel>(
            "school", "subscribe-student", new TestCommand("student-1", Attempt: 1));

        PrepareDecisionRequest request = Assert.IsType<PrepareDecisionRequest>(invoker.Request);
        Assert.Equal("{\"studentId\":\"student-1\",\"attempt\":1}", request.CommandJson.ToStringUtf8());
        Assert.Same(response, prepared.Response);
        Assert.Equal(PrepareDecisionResponse.OutcomeOneofCase.Prepared, prepared.OutcomeCase);
        Assert.Equal(Guid.Parse("83d4527d-e6f8-4dce-8799-eafc2d0b6400"), prepared.CommandId);
        Assert.Equal("subscribe", prepared.CommandType);
        Assert.Equal(new TestModel("student-1", RemainingSeats: 4), prepared.Model);
        Assert.Same(signature, prepared.ModelSignature);
        Assert.Equal(expiry, prepared.ExpiresUtc);
        Assert.Equal("sha256:plan", prepared.PlanFingerprint);
    }

    [Fact]
    public async Task Prepare_preserves_non_prepared_generated_outcome()
    {
        PrepareDecisionResponse response = new()
        {
            CommandId = "83d4527d-e6f8-4dce-8799-eafc2d0b6400",
            CommandType = "subscribe",
            AlreadyCommitted = new AlreadyCommitted { FirstEventId = 10, LastEventId = 11 }
        };
        using NativeDcbClient client = CreateClient(new UnaryCallInvoker(_ => response));

        PreparedDecision<TestModel> result = await client.PrepareDecisionAsync<TestCommand, TestModel>(
            "school", "subscribe-student", new TestCommand("student-1", Attempt: 1));

        Assert.Same(response, result.Response);
        Assert.Equal(PrepareDecisionResponse.OutcomeOneofCase.AlreadyCommitted, result.OutcomeCase);
        Assert.False(result.IsPrepared);
        Assert.Throws<InvalidOperationException>(() => result.Model);
    }

    [Fact]
    public async Task Prepare_rejects_malformed_model_json()
    {
        PrepareDecisionResponse response = new()
        {
            CommandId = "83d4527d-e6f8-4dce-8799-eafc2d0b6400",
            CommandType = "subscribe",
            Prepared = new PreparedDecision
            {
                ModelJson = ByteString.CopyFromUtf8("{not-json"),
                ModelSignature = ByteString.CopyFromUtf8("signature"),
                ExpiresUtc = Timestamp.FromDateTimeOffset(DateTimeOffset.UtcNow),
                PlanFingerprint = "plan"
            }
        };
        using NativeDcbClient client = CreateClient(new UnaryCallInvoker(_ => response));

        await Assert.ThrowsAsync<JsonException>(() => client.PrepareDecisionAsync<TestCommand, TestModel>(
            "school", "subscribe-student", new TestCommand("student-1", Attempt: 1)));
    }

    [Fact]
    public async Task Complete_serializes_runtime_event_data_without_keys_and_preserves_outcome()
    {
        ByteString signature = ByteString.CopyFromUtf8("opaque-signature");
        CompleteDecisionResponse response = new()
        {
            CommandId = "83d4527d-e6f8-4dce-8799-eafc2d0b6400",
            CommandType = "subscribe",
            Stale = new DecisionStale { CurrentHead = 42 }
        };
        UnaryCallInvoker invoker = new(_ => response);
        JsonSerializerOptions options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        using NativeDcbClient client = CreateClient(invoker, options);
        object eventData = new TestEvent("student-1", CourseCapacity: 20);

        CompleteDecisionResponse actual = await client.CompleteDecisionAsync(
            "school", signature, [new ProposedDecisionEvent("student-subscribed", eventData)]);

        CompleteDecisionRequest request = Assert.IsType<CompleteDecisionRequest>(invoker.Request);
        Assert.Equal("school", request.Database);
        Assert.Same(signature, request.ModelSignature);
        ProposedEvent proposed = Assert.Single(request.ProposedEvents);
        Assert.Equal("student-subscribed", proposed.Type);
        Assert.Equal("{\"studentId\":\"student-1\",\"courseCapacity\":20}", proposed.DataJson.ToStringUtf8());
        Assert.Same(response, actual);
        Assert.Equal(CompleteDecisionResponse.OutcomeOneofCase.Stale, actual.OutcomeCase);
    }

    [Fact]
    public void Range_request_maps_bounds_limit_and_mode()
    {
        ReadEventsByRangeRequest request = NativeDcbClient.BuildReadEventsByRangeRequest("db", afterEventId: 10,
            throughEventId: 20, limit: 25, ReadMode.Follow);

        Assert.Equal("db", request.Database);
        Assert.Equal(expected: 10, request.AfterEventId);
        Assert.True(request.HasThroughEventId);
        Assert.Equal(expected: 20, request.ThroughEventId);
        Assert.True(request.HasLimit);
        Assert.Equal(expected: 25u, request.Limit);
        Assert.Equal(ReadMode.Follow, request.Mode);
    }

    [Fact]
    public void Query_request_maps_model_query_without_losing_order()
    {
        EventQuery query = new(
        [
            new QueryItem(["first", "second"], [new EventKey("student", "s-1"), new EventKey("course", "c-1")]),
            new QueryItem(["third"], [])
        ]);

        ReadEventsByQueryRequest request = NativeDcbClient.BuildReadEventsByQueryRequest(
            "db", query, afterEventId: 4, throughEventId: 8, limit: 2, QueryConsistency.EventualIndex);

        Assert.Equal(expected: 4, request.AfterEventId);
        Assert.Equal(expected: 8, request.ThroughEventId);
        Assert.Equal(expected: 2u, request.Limit);
        Assert.Equal(QueryConsistency.EventualIndex, request.Consistency);
        Assert.Equal(["first", "second"], request.Query.Items[index: 0].EventTypes);
        Assert.Equal(["student", "course"], request.Query.Items[index: 0].Keys.Select(key => key.Key));
        Assert.Equal(["s-1", "c-1"], request.Query.Items[index: 0].Keys.Select(key => key.Value));
        Assert.Equal("third", request.Query.Items[index: 1].EventTypes.Single());
    }

    [Fact]
    public void Command_read_handler_and_validation_requests_map_all_fields()
    {
        Guid commandId = Guid.Parse("0180e76d-87ca-7a8f-9072-cffe3719a791");
        GetEventsByCommandIdRequest commandRead = NativeDcbClient.BuildGetEventsByCommandIdRequest("db", commandId);
        RegisterHandlerRequest registration = NativeDcbClient.BuildRegisterHandlerRequest(
            "db", "handler", "command-type", "on command", allowIncompatible: true);
        ValidateNdlRequest validation = NativeDcbClient.BuildValidateNdlRequest("db", "validate this");

        Assert.Equal(commandId.ToString("D"), commandRead.CommandId);
        Assert.Equal(("db", "handler", "command-type", "on command", true),
            (registration.Database, registration.HandlerName, registration.CommandType,
                registration.NdlSource, registration.AllowIncompatible));
        Assert.Equal(("db", "validate this"), (validation.Database, validation.NdlSource));
    }

    [Fact]
    public void Event_envelope_maps_to_storage_neutral_model()
    {
        DateTimeOffset timestamp = DateTimeOffset.Parse("2026-08-13T12:34:56Z");
        Guid commandId = Guid.Parse("fbe4c5e3-937f-469a-b972-f4e02f9791ea");
        EventEnvelope envelope = new()
        {
            EventId = 42,
            Type = "course-defined",
            SchemaVersion = 2,
            DataJson = ByteString.CopyFromUtf8("{\"capacity\":20}"),
            TimestampUtc = Timestamp.FromDateTimeOffset(timestamp),
            CommandId = commandId.ToString("D"),
            CommandType = "define-course"
        };
        envelope.Keys.Add(new KeyValue { Key = "course", Value = "course-1" });

        SequencedEvent mapped = NativeDcbClient.MapEvent(envelope);

        Assert.Equal(expected: 42, mapped.EventId);
        Assert.Equal("course-defined", mapped.Type);
        Assert.Equal(expected: 20, mapped.Data.GetProperty("capacity").GetInt32());
        Assert.Equal(new EventKey("course", "course-1"), mapped.Keys.Single());
        Assert.Equal(expected: 2u, mapped.SchemaVersion);
        Assert.Equal(timestamp, mapped.TimestampUtc);
        Assert.Equal(commandId, mapped.CommandId);
        Assert.Equal("define-course", mapped.CommandType);
    }

    [Theory]
    [InlineData("", "handler")]
    [InlineData("db", " ")]
    public void Execute_request_rejects_missing_routing_values(string database, string handler)
    {
        Assert.Throws<ArgumentException>(() =>
            NativeDcbClient.BuildExecuteHandlerRequest(database, handler, new TestCommand("s", Attempt: 1)));
    }

    [Fact]
    public void Range_request_rejects_invalid_event_bounds()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeDcbClient.BuildReadEventsByRangeRequest("db", afterEventId: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            NativeDcbClient.BuildReadEventsByRangeRequest("db", afterEventId: 10, throughEventId: 9));
    }

    private static NativeDcbClient CreateClient(
        CallInvoker invoker,
        JsonSerializerOptions? jsonOptions = null)
    {
        return new NativeDcbClient(
            new CommandService.CommandServiceClient(invoker),
            new EventService.EventServiceClient(invoker),
            new CatalogService.CatalogServiceClient(invoker),
            jsonOptions);
    }

    private sealed record TestCommand(
        // ReSharper disable once NotAccessedPositionalProperty.Local
        string StudentId,
        // ReSharper disable once NotAccessedPositionalProperty.Local
        int Attempt);

    private sealed record TestModel(string StudentId, int RemainingSeats);

    private sealed record TestEvent(string StudentId, int CourseCapacity);

    private sealed class UnaryCallInvoker(Func<object, object> handler) : CallInvoker
    {
        public object? Request { get; private set; }

        public override AsyncUnaryCall<TResponse> AsyncUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            Request = request;
            TResponse response = (TResponse)handler(request);
            return new AsyncUnaryCall<TResponse>(
                Task.FromResult(response),
                Task.FromResult(new Metadata()),
                () => Status.DefaultSuccess,
                () => [],
                () => { });
        }

        public override TResponse BlockingUnaryCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            throw new NotSupportedException();
        }

        public override AsyncClientStreamingCall<TRequest, TResponse> AsyncClientStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
        {
            throw new NotSupportedException();
        }

        public override AsyncServerStreamingCall<TResponse> AsyncServerStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options,
            TRequest request)
        {
            throw new NotSupportedException();
        }

        public override AsyncDuplexStreamingCall<TRequest, TResponse> AsyncDuplexStreamingCall<TRequest, TResponse>(
            Method<TRequest, TResponse> method,
            string? host,
            CallOptions options)
        {
            throw new NotSupportedException();
        }
    }
}