using NativeDCB.Generated;
using NativeDCB.Protocol.V1;
using NativeDCB.Sdk;

const string usage = "Usage: CourseSubscriptions <seed|run> [server-address] [database]";
if (args.Length == 0 || args[0] is "--help" or "-h")
{
    Console.WriteLine(usage);
    return 0;
}

string mode = args[0].ToLowerInvariant();
if (mode is not ("seed" or "run") || args.Length > 3)
{
    Console.Error.WriteLine(usage);
    return 64;
}

string address = args.ElementAtOrDefault(index: 1) ?? "http://localhost:5010";
string database = args.ElementAtOrDefault(index: 2) ?? "school";
using NativeDcbClient client = new(address);

try
{
    await SeedAsync(client, database);
    if (mode == "seed")
    {
        Console.WriteLine($"Seeded database '{database}' at {address}.");
        return 0;
    }

    await RunScenarioAsync(client, database);
    return 0;
}
catch (Exception exception)
{
    Console.Error.WriteLine($"CourseSubscriptions {mode} failed: {exception.Message}");
    return 1;
}

static async Task SeedAsync(NativeDcbClient client, string database)
{
    Console.WriteLine($"Generated {NativeDcbGeneratedSchemas.Create().Count} schema descriptors.");
    ListDatabasesResponse databases = await client.ListDatabasesAsync();
    if (databases.Databases.All(value => value.Database != database))
    {
        await client.CreateDatabaseAsync(database);
        Console.WriteLine($"Created database '{database}'.");
    }

    RegisterSchemaResponse courseSchema = await client.RegisterEventSchemaAsync<CourseDefined>(database);
    RegisterSchemaResponse subscriptionSchema =
        await client.RegisterEventSchemaAsync<StudentSubscribedToCourse>(database);
    RegisterSchemaResponse defineSchema = await client.RegisterCommandSchemaAsync<DefineCourse>(database);
    RegisterSchemaResponse subscribeSchema =
        await client.RegisterCommandSchemaAsync<SubscribeStudentToCourse>(database);
    EnsureSchemaValid("CourseDefined", courseSchema);
    EnsureSchemaValid("StudentSubscribedToCourse", subscriptionSchema);
    EnsureSchemaValid("DefineCourse", defineSchema);
    EnsureSchemaValid("SubscribeStudentToCourse", subscribeSchema);

    string ndl = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "CourseSubscriptions.ndl"));
    List<StatementResult> statementResults = [];
    await foreach (StatementResult result in client.ExecuteStatementAsync(database, ndl))
    {
        statementResults.Add(result);
        Console.WriteLine($"NDL statement {result.StatementIndex}: {result.ResultCase}");
    }

    StatementCompletion? completion = statementResults
        .SingleOrDefault(result => result.ResultCase == StatementResult.ResultOneofCase.Completion)
        ?.Completion;
    if (completion?.Succeeded != true)
    {
        string diagnostics = string.Join(
            Environment.NewLine,
            statementResults
                .Where(result => result.ResultCase == StatementResult.ResultOneofCase.Diagnostics)
                .SelectMany(result => result.Diagnostics.Diagnostics)
                .Select(diagnostic => $"{diagnostic.Code}: {diagnostic.Message}"));
        throw new InvalidOperationException(
            $"NDL handler registration failed: {completion?.Summary ?? "No completion received."}{Environment.NewLine}{diagnostics}"
                .Trim());
    }

    string[] registeredNdlHandlers = statementResults
        .Where(result => result.ResultCase == StatementResult.ResultOneofCase.Registration)
        .Select(result => result.Registration.Name)
        .Order(StringComparer.Ordinal)
        .ToArray();
    if (!registeredNdlHandlers.SequenceEqual(["DefineCourse", "SubscribeStudentNdl"]))
    {
        throw new InvalidOperationException(
            $"Expected both NDL handlers, but registered: {string.Join(", ", registeredNdlHandlers)}.");
    }

    RegisterHandlerResponse sdkHandler = await client.RegisterDecisionAsync(
        database,
        "SubscribeStudentSdk",
        CreateSdkDefinition());
    if (sdkHandler.Handler?.Valid != true)
    {
        throw new InvalidOperationException("SDK handler registration failed.");
    }
}

static async Task RunScenarioAsync(NativeDcbClient client, string database)
{
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
}

static DecisionDefinition<SubscribeStudentToCourse> CreateSdkDefinition()
{
    SubscribeStudentToCourse template = new("student-template", "course-template");
    return Decision.WithDecisionModel(template)
        .Include<CourseDefined, CourseModel>((_, _) => new CourseModel(true))
        .Where(@event => @event.CourseId == template.CourseId)
        .Evaluate((model, _) => model.CourseExists)
        .Decide((accepted, command) => accepted
            ? Decision.Accept(new StudentSubscribedToCourse(command.StudentId, command.CourseId))
            : Decision.Reject("Course does not exist"));
}

static void EnsureSchemaValid(string name, RegisterSchemaResponse response)
{
    Diagnostic[] errors = response.Diagnostics
        .Where(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
        .ToArray();
    if (errors.Length > 0)
    {
        throw new InvalidOperationException(
            $"Schema '{name}' is invalid: {string.Join(" / ", errors.Select(error => error.Message))}");
    }
}

[EventType("CourseDefined")]
// ReSharper disable once ClassNeverInstantiated.Global
public sealed record CourseDefined(
    [property: ConsistencyKey("course")] string CourseId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    int Capacity);

[EventType("StudentSubscribedToCourse")]
public sealed record StudentSubscribedToCourse(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    [property: ConsistencyKey("student")] string StudentId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    [property: ConsistencyKey("course")] string CourseId);

[CommandType("SubscribeStudentToCourse")]
public sealed record SubscribeStudentToCourse(string StudentId, string CourseId);

[CommandType("DefineCourse")]
public sealed record DefineCourse(
    // ReSharper disable once NotAccessedPositionalProperty.Global
    string CourseId,
    // ReSharper disable once NotAccessedPositionalProperty.Global
    int Capacity);

public sealed record CourseModel(bool CourseExists);