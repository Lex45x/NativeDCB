using NativeDCB.Model;
using NativeDCB.Model.Decisions;
using NativeDCB.Sdk;
using NativeDCB.Sdk.Decisions.Authoring;
using NativeDCB.Sdk.Schemas;

namespace NativeDCB.EndToEndTests.Authoring;

public sealed class AuthoringEquivalenceTests
{
    private const string Source = """
                                  decision SubscribeStudent
                                  from Subscribe command
                                  | include CourseDefined event
                                      where event.CourseId == command.CourseId
                                      apply { Exists = true }
                                  | evaluate {
                                      require (model.Exists ?? false) else "Course does not exist";
                                  }
                                  | decide {
                                      emit Subscribed {
                                          StudentId = command.StudentId,
                                          CourseId = command.CourseId
                                      };
                                  };
                                  """;

    [Fact]
    public void NdlAndSdkProduceEquivalentPlanBoundaries()
    {
        DecisionPlan ndl = Assert.Single(Ndl.Ndl.Compile(Source).Plans);
        Subscribe command = new("student", "course");
        DecisionPlan sdk = Decision.WithDecisionModel(command)
            .Include<CourseDefined, CourseModel>((_, _) => new CourseModel(true))
            .Where(@event => @event.CourseId == command.CourseId)
            .Evaluate((model, _) => model.Exists)
            .Decide((accepted, input) => accepted
                ? Decision.Accept(new Subscribed(input.StudentId, input.CourseId))
                : Decision.Reject("Course does not exist"))
            .Compile("SubscribeStudent");

        Assert.Equal(ndl.Name, sdk.Name);
        Assert.Equal(ndl.CommandSchema, sdk.CommandSchema);
        Assert.Equal(
            ndl.Includes.Select(value => (value.EventType, Keys: value.KeyBindings.Select(key => key.PropertyName))),
            sdk.Includes.Select(value => (value.EventType, Keys: value.KeyBindings.Select(key => key.PropertyName))));
        Assert.Equal(
            ndl.Emissions.Select(value => (value.EventType, Properties: value.Assignments.Select(item => item.Name))),
            sdk.Emissions.Select(value => (value.EventType, Properties: value.Assignments.Select(item => item.Name))));
        Assert.Equal("ndl-v1", ndl.Fingerprints.LanguageVersion);
        Assert.Equal("sdk-v1", sdk.Fingerprints.LanguageVersion);
    }

    [CommandType("Subscribe")]
    private sealed record Subscribe(string StudentId, string CourseId);

    [EventType("CourseDefined")]
    // ReSharper disable once ClassNeverInstantiated.Local
    private sealed record CourseDefined([property: ConsistencyKey("course")] string CourseId);

    [EventType("Subscribed")]
    private sealed record Subscribed(
        // ReSharper disable once NotAccessedPositionalProperty.Local
        [property: ConsistencyKey("student")] string StudentId,
        // ReSharper disable once NotAccessedPositionalProperty.Local
        [property: ConsistencyKey("course")] string CourseId);

    private sealed record CourseModel(bool Exists);
}
