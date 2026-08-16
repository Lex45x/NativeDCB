using System.Text.Json;

using Google.Protobuf;
using Google.Protobuf.WellKnownTypes;

using NativeDCB.Model;
using NativeDCB.Protocol.V1;

using QueryItem = NativeDCB.Model.QueryItem;

namespace NativeDCB.Sdk.Tests;

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

    private sealed record TestCommand(
        // ReSharper disable once NotAccessedPositionalProperty.Local
        string StudentId,
        // ReSharper disable once NotAccessedPositionalProperty.Local
        int Attempt);
}