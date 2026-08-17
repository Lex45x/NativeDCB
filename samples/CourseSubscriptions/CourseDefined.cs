using NativeDCB.Sdk;

[EventType("CourseDefined")]
// ReSharper disable once ClassNeverInstantiated.Global
public sealed record CourseDefined(
    [property: ConsistencyKey("course")] string CourseId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    int Capacity);