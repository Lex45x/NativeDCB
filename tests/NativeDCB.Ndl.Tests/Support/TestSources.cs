namespace NativeDCB.Ndl.Tests;

internal static class TestSources
{
    public const string Complete = """
                                   decision SubscribeStudentToCourse
                                   from Contracts.SubscribeStudentToCourse command
                                   | include CourseDefined event
                                       where event.CourseId == command.CourseId
                                       apply {
                                           CourseExists = true,
                                           CourseCapacity = event.Capacity
                                       }
                                   | include StudentSubscribedToCourse event
                                       where event.StudentId == command.StudentId and event.CourseId == command.CourseId
                                       apply {
                                           CourseSubscriptions = (previous.CourseSubscriptions ?? 0) + 1,
                                           LastPosition = position
                                       }
                                   | evaluate {
                                       require model.CourseExists else "Course does not exist";
                                       require not model.AlreadySubscribed and model.CourseSubscriptions < model.CourseCapacity
                                           else "Cannot subscribe";
                                       let remainingSeats = model.CourseCapacity - model.CourseSubscriptions;
                                   }
                                   | decide {
                                       emit StudentSubscribedToCourse {
                                           StudentId = command.StudentId,
                                           CourseId = command.CourseId,
                                           Seats = remainingSeats > 0 ? remainingSeats : 0
                                       };
                                       emit AuditEntryRecorded {
                                           Message = "subscribed"
                                       };
                                   };
                                   """;

    public static string DecisionWithExpression(string expression)
    {
        return $$"""
                 decision Test
                 from Command command
                 | include Event event
                     where event.Id == command.Id
                     apply { Value = {{expression}} }
                 | evaluate { }
                 | decide { emit Result { Value = model.Value }; };
                 """;
    }
}