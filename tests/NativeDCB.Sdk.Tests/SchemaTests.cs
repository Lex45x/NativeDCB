using NativeDCB.Model;

namespace NativeDCB.Sdk.Tests;

public sealed class SchemaTests
{
    [Fact]
    public void Attributes_expose_stable_names()
    {
        EventTypeAttribute eventAttribute = new("course-defined");
        CommandTypeAttribute commandAttribute = new("define-course");
        ConsistencyKeyAttribute keyAttribute = new("course");

        Assert.Equal("course-defined", eventAttribute.Name);
        Assert.Equal(eventAttribute.Name, eventAttribute.Identifier);
        Assert.Equal("define-course", commandAttribute.Name);
        Assert.Equal(commandAttribute.Name, commandAttribute.Identifier);
        Assert.Equal("course", keyAttribute.Name);
        Assert.Equal(keyAttribute.Name, keyAttribute.Tag);
    }

    [Fact]
    public void Attributes_reject_blank_names()
    {
        Assert.Throws<ArgumentException>(() => new EventTypeAttribute(" "));
        Assert.Throws<ArgumentException>(() => new CommandTypeAttribute(""));
        Assert.Throws<ArgumentException>(() => new ConsistencyKeyAttribute(" "));
    }

    [Fact]
    public void Event_descriptor_extracts_deterministic_tags_in_property_order()
    {
        SchemaDescriptor descriptor = SchemaDescriptor.ForEvent<TaggedEvent>();

        IReadOnlyList<EventKey> tags = descriptor.ExtractTags(new TaggedEvent("student-1",
            Guid.Parse("9f9bda6c-3726-4f89-98b5-fcb80e692188"), Attempt: 7));

        Assert.Equal("tagged-event", descriptor.Name);
        Assert.Equal(SchemaType.Event, descriptor.SchemaType);
        Assert.Equal(typeof(TaggedEvent), descriptor.ClrType);
        Assert.Collection(
            descriptor.ConsistencyKeys,
            key => Assert.Equal("student", key.Name),
            key => Assert.Equal("course", key.Name),
            key => Assert.Equal("attempt", key.Name));
        Assert.Collection(
            tags,
            tag => Assert.Equal(("student", "student-1"), (tag.Name, tag.Value)),
            tag => Assert.Equal(("course", "9f9bda6c-3726-4f89-98b5-fcb80e692188"), (tag.Name, tag.Value)),
            tag => Assert.Equal(("attempt", "7"), (tag.Name, tag.Value)));
    }

    [Fact]
    public void Command_descriptor_uses_command_attribute()
    {
        SchemaDescriptor descriptor = SchemaDescriptor.ForCommand<TestCommand>();

        Assert.Equal("test-command", descriptor.Name);
        Assert.Equal(SchemaType.Command, descriptor.SchemaType);
    }

    [Fact]
    public void Invalid_event_schemas_are_rejected()
    {
        Assert.Throws<InvalidOperationException>(SchemaDescriptor.ForEvent<MissingEventAttribute>);
        Assert.Throws<InvalidOperationException>(SchemaDescriptor.ForEvent<MissingKey>);
        Assert.Throws<InvalidOperationException>(SchemaDescriptor.ForEvent<DuplicateKeys>);
    }

    [Fact]
    public void Extract_tags_rejects_null_and_wrong_instances()
    {
        SchemaDescriptor descriptor = SchemaDescriptor.ForEvent<NullableKeyEvent>();

        Assert.Throws<InvalidOperationException>(() => descriptor.ExtractTags(new NullableKeyEvent(Id: null)));
        Assert.Throws<ArgumentException>(() => descriptor.ExtractTags(new object()));
    }

    // Schema fixture members are discovered and accessed through reflection.
    // ReSharper disable ClassNeverInstantiated.Local
    // ReSharper disable NotAccessedPositionalProperty.Local
    [EventType("tagged-event")]
    private sealed record TaggedEvent(
        [property: ConsistencyKey("student")] string StudentId,
        [property: ConsistencyKey("course")] Guid CourseId,
        [property: ConsistencyKey("attempt")] int Attempt);

    [CommandType("test-command")]
    private sealed record TestCommand(string Id);

    private sealed record MissingEventAttribute(string Id);

    [EventType("missing-key")]
    private sealed record MissingKey(string Id);

    [EventType("duplicate-keys")]
    private sealed record DuplicateKeys(
        [property: ConsistencyKey("same")] string First,
        [property: ConsistencyKey("same")] string Second);

    [EventType("nullable-key")]
    private sealed record NullableKeyEvent([property: ConsistencyKey("id")] string? Id);
    // ReSharper restore NotAccessedPositionalProperty.Local
    // ReSharper restore ClassNeverInstantiated.Local
}