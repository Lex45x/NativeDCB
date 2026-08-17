using NativeDCB.Sdk;

[CommandType("DefineCourse")]
public sealed record DefineCourse(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string CourseId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    int Capacity);