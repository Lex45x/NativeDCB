using NativeDCB.Sdk;

namespace CourseSubscriptions;

[CommandType("SubscribeStudentToCourse")]
public sealed record SubscribeStudentToCourse(string StudentId, string CourseId);