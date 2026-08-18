using NativeDCB.Sdk;
using NativeDCB.Sdk.Schemas;

namespace CourseSubscriptions.Commands;

[CommandType("SubscribeStudentToCourse")]
public sealed record SubscribeStudentToCourse(string StudentId, string CourseId);