using NativeDCB.Sdk;

[CommandType("SubscribeStudentToCourse")]
public sealed record SubscribeStudentToCourse(string StudentId, string CourseId);