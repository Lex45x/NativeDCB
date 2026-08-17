using NativeDCB.Sdk;

namespace CourseSubscriptions;

[EventType("StudentSubscribedToCourse")]
public sealed record StudentSubscribedToCourse(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    [property: ConsistencyKey("student")] string StudentId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    [property: ConsistencyKey("course")] string CourseId);