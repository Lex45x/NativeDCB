using CourseSubscriptions.Commands;
using CourseSubscriptions.Events;
using CourseSubscriptions.Models;

// ReSharper disable once RedundantUsingDirective -- Provided by the source generator during compilation.
using NativeDCB.Generated;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk;
using NativeDCB.Sdk.Client;
using NativeDCB.Sdk.Decisions.Authoring;

string mode = args.ElementAtOrDefault(index: 0) ?? "run";
string address = args.ElementAtOrDefault(index: 1) ?? "http://localhost:5010";
string database = args.ElementAtOrDefault(index: 2) ?? "school";
if (mode is not ("seed" or "run" or "remote"))
{
    Console.Error.WriteLine("Usage: CourseSubscriptions [seed|run|remote] [address] [database]");
    Environment.ExitCode = 2;
    return;
}

using NativeDcbClient client = new(address);

Console.WriteLine($"Generated {NativeDcbGeneratedSchemas.Create().Count} schema descriptors.");
await SeedAsync(client, database);
Console.WriteLine($"Seeded database '{database}'.");
if (mode == "seed")
{
    return;
}

if (mode == "remote")
{
    ExecuteHandlerResponse remoteCourse = await client.ExecuteHandlerAsync(
        database,
        "DefineCourse",
        new DefineCourse("native-dcb", Capacity: 30));
    SubscribeStudentToCourse command = new("student-remote", "native-dcb");
    PreparedDecision<CourseModel> prepared = await client.PrepareDecisionAsync<
        SubscribeStudentToCourse, CourseModel>(database, "SubscribeStudentSdk", command);
    if (!prepared.IsPrepared)
    {
        Console.WriteLine($"Remote SubscribeStudentSdk: {prepared.OutcomeCase}");
        return;
    }

    if (!prepared.Model.CourseExists)
    {
        Console.WriteLine("Remote SubscribeStudentSdk: Rejected (Course does not exist)");
        return;
    }

    CompleteDecisionResponse completed = await client.CompleteDecisionAsync(
        database,
        prepared.ModelSignature,
        [
            new ProposedDecisionEvent(
                "StudentSubscribedToCourse",
                new StudentSubscribedToCourse(command.StudentId, command.CourseId))
        ]);

    Console.WriteLine($"Remote DefineCourse: {remoteCourse.OutcomeCase}");
    Console.WriteLine($"Remote SubscribeStudentSdk: {completed.OutcomeCase}");
    return;
}

ExecuteHandlerResponse define = await client.ExecuteHandlerAsync(
    database,
    "DefineCourse",
    new DefineCourse("native-dcb", Capacity: 30));
ExecuteHandlerResponse ndlSubscription = await client.ExecuteHandlerAsync(
    database,
    "SubscribeStudentNdl",
    new SubscribeStudentToCourse("student-ndl", "native-dcb"));
ExecuteHandlerResponse sdkSubscription = await client.ExecuteHandlerAsync(
    database,
    "SubscribeStudentSdk",
    new SubscribeStudentToCourse("student-sdk", "native-dcb"));

Console.WriteLine($"DefineCourse: {define.OutcomeCase}");
Console.WriteLine($"SubscribeStudentNdl: {ndlSubscription.OutcomeCase}");
Console.WriteLine($"SubscribeStudentSdk: {sdkSubscription.OutcomeCase}");

static async Task SeedAsync(NativeDcbClient client, string database)
{
    ListDatabasesResponse databases = await client.ListDatabasesAsync();
    if (databases.Databases.All(value => value.Database != database))
    {
        await client.CreateDatabaseAsync(database);
        Console.WriteLine($"Created database '{database}'.");
    }

    await client.RegisterEventSchemaAsync<CourseDefined>(database);
    await client.RegisterEventSchemaAsync<StudentSubscribedToCourse>(database);
    await client.RegisterCommandSchemaAsync<DefineCourse>(database);
    await client.RegisterCommandSchemaAsync<SubscribeStudentToCourse>(database);

    string ndl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "CourseSubscriptions.ndl"));
    await foreach (StatementResult result in client.ExecuteStatementAsync(database, ndl))
    {
        Console.WriteLine($"NDL statement {result.StatementIndex}: {result.ResultCase}");
    }

    SubscribeStudentToCourse template = new("student-template", "course-template");
    DecisionDefinition<SubscribeStudentToCourse> sdkDefinition = Decision.WithDecisionModel(template)
        .Include<CourseDefined, CourseModel>((_, @event) => new CourseModel(true))
        .Where(@event => @event.CourseId == template.CourseId)
        .Evaluate((model, _) => model.CourseExists)
        .Decide((accepted, command) => accepted
            ? Decision.Accept(new StudentSubscribedToCourse(command.StudentId, command.CourseId))
            : Decision.Reject("Course does not exist"));
    await client.RegisterDecisionAsync(database, "SubscribeStudentSdk", sdkDefinition);
}