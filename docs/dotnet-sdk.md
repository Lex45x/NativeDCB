# NativeDCB .NET SDK

Status: current runtime, fluent API, analyzer, and generator reference  
Last verified: 2026-08-14

## Packages And Projects

The repository has three SDK-related projects:

- `NativeDCB.Sdk` (`net10.0`): runtime schema, decision-plan, and gRPC client APIs
- `NativeDCB.Sdk.Analyzers` (`netstandard2.0`): schema diagnostics
- `NativeDCB.Sdk.Generators` (`netstandard2.0`): schema factory generation

The `NativeDCB.Sdk` project has private build references to the analyzer and generator and configures its NuGet package to embed both DLLs under `analyzers/dotnet/cs`. The standalone `NativeDCB.Sdk.Analyzers` and `NativeDCB.Sdk.Generators` package projects are also packable and place their DLLs in the standard `analyzers/dotnet/cs` asset path.

For source-project development in this repository, `samples/CourseSubscriptions` references both Roslyn projects explicitly as analyzers:

```xml
<ProjectReference Include="..\..\src\NativeDCB.Sdk.Analyzers\NativeDCB.Sdk.Analyzers.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
<ProjectReference Include="..\..\src\NativeDCB.Sdk.Generators\NativeDCB.Sdk.Generators.csproj"
                  OutputItemType="Analyzer" ReferenceOutputAssembly="false" />
```

Running `dotnet pack` can therefore produce the SDK package with both build tools included, or either standalone build-tool package. The repository does not currently contain package publishing, signing, or release automation.

## Schema API

Declare stable protocol names and direct consistency-key properties:

```csharp
[EventType("StudentSubscribedToCourse")]
public sealed record StudentSubscribedToCourse(
    [property: ConsistencyKey("student")] string StudentId,
    [property: ConsistencyKey("course")] string CourseId);

[CommandType("SubscribeStudentToCourse")]
public sealed record SubscribeStudentToCourse(string StudentId, string CourseId);
```

`SchemaDescriptor.ForEvent<T>()` requires `[EventType]` and at least one readable direct `[ConsistencyKey]` property. `ForCommand<T>()` requires `[CommandType]`; command keys are optional. Descriptors expose CLR type, stable name, schema kind, key descriptors, `ExtractTags(instance)`, and `ToJsonSchemaDocument()`.

Consistency-key encoding is invariant and supports string, GUID, specified `DateTime`, `DateTimeOffset`, Boolean, enum, integer types, and decimal. Null, unspecified `DateTime`, floating point, collections, and arbitrary objects are rejected as keys. Event key names must be unique.

Generated schema documents use the server's supported profile:

```json
{
  "type": "object",
  "properties": {
    "StudentId": {
      "type": "string",
      "x-native-dcb-consistency-key": "student"
    }
  },
  "required": ["StudentId"],
  "additionalProperties": false
}
```

Public readable instance properties are included. CLR nullability determines `required`; nullable value types are unwrapped for JSON type selection. Types are mapped coarsely to string/integer/number/boolean/array/object. The generated document is not a complete serializer contract and does not describe nested members.

## Fluent Decision API

The implemented type-state flow is:

```text
Decision.WithDecisionModel(command)
  -> DecisionModelBuilder<TCommand, EmptyDecisionModel>
Include<TEvent, TNext>(reducer)
  -> DecisionModelEventSpec<...>
Where(predicate)
  -> DecisionModelBuilder<TCommand, TNext>
Evaluate(expression)
  -> EvaluatedDecision<...>
Decide(expression)
  -> DecisionDefinition<TCommand>
Compile(name)
  -> DecisionPlan
```

Only `Where` is available after `Include`; only `Decide` is available after `Evaluate`. `Include<TEvent>(...)` is also available when previous and next model types are the same.

```csharp
var template = new SubscribeStudentToCourse("student-template", "course-template");

DecisionDefinition<SubscribeStudentToCourse> definition =
    Decision.WithDecisionModel(template)
        .Include<CourseDefined, CourseModel>((_, @event) =>
            new CourseModel(true, @event.Capacity))
        .Where(@event => @event.CourseId == template.CourseId)
        .Evaluate((model, _) => model.Exists)
        .Decide((accepted, command) => accepted
            ? Decision.Accept(new StudentSubscribedToCourse(
                command.StudentId, command.CourseId))
            : Decision.Reject("Course does not exist"));

DecisionPlan plan = definition.Compile("SubscribeStudentSdk");
```

The template command supplies expression-tree captures and query-template values at definition/compilation time. The resulting plan refers to runtime `command` properties, so the registered plan can execute other command instances.

`DecisionDefinition` exposes the template command, includes, translated query templates, evaluation/decision expression trees, SDK diagnostics, `IsValid`, and `Compile(name)`.

### Where translation

`Where` accepts only equality expressions joined by `&&`. Either equality side may be a direct property of the event parameter; that property must have `[ConsistencyKey]`. The other side must not reference the event and must be evaluable from the captured command/constants while the definition is built. Logical key values are encoded immediately for `QueryTemplates`.

The plan compiler independently translates the predicate to command-referencing plan expressions and emits `PlanKeyBinding`s using CLR property names. The server maps those names through registered schemas at runtime.

### Reducers, evaluation, and decision

Reducers and evaluation are expression trees. Reducers must lower to a constructor/member-init structural object. The server applies their assignments as patches during globally ordered replay.

`Evaluate` becomes a plan local named `evaluation`. `Decide` supports only:

- direct `Decision.Accept(event1, ...)`
- a conditional whose true branch is `Decision.Accept(...)` and false branch is `Decision.Reject(reason)`

Accepted events must be constructor/member-init expressions for attributed event types. At least one emission is required. The conditional becomes a `PlanRequirement`, so rejection occurs before emission.

The expression translator currently supports literals, command/model/event member access, object construction/member initialization, arithmetic/comparison/Boolean/coalesce operators, conditional expressions, and `Math.Min`/`Math.Max`. Unsupported nodes/methods throw during `Compile`. There is no comprehensive Roslyn fluent-expression analyzer yet.

## Plan API And Registration

`Compile(name)` returns the shared `NativeDCB.Model.DecisionPlan` with:

- stable decision name and command schema
- keyed include/query templates and reducer assignments
- evaluation local/require steps
- ordered emissions
- language version `sdk-v1`
- a source fingerprint derived from expression text
- SHA-256 fingerprints of generated command, included-event, and emitted-event schemas

Register a plan directly:

```csharp
using var client = new NativeDcbClient("http://localhost:5010");

await client.RegisterEventSchemaAsync<CourseDefined>("school");
await client.RegisterEventSchemaAsync<StudentSubscribedToCourse>("school");
await client.RegisterCommandSchemaAsync<SubscribeStudentToCourse>("school");
await client.RegisterDecisionAsync("school", "SubscribeStudentSdk", definition);
```

`RegisterDecisionAsync` serializes the plan into `RegisterHandlerRequest.plan_json`; it does not generate NDL during registration. The server validates language/source fields, keyed includes, non-empty emissions, known key properties, and any matching current schema fingerprints before persistence. `GetHandlerAsync` can later request the stored plan and a best-effort canonical NDL representation; generated NDL is separate from original source and can report conversion diagnostics.

## Client API

`NativeDcbClient` wraps every RPC in the six-service protocol. Its address constructor owns a `GrpcChannel` and enables all services. A constructor accepting generated Database, Catalog, Command, Event, Statement, and Administration clients also enables all services for dependency-injection/testing scenarios. The legacy constructor accepting only Command, Event, and Catalog clients preserves the earlier surface; calling a newer Database, Statement, or Administration wrapper on such an instance throws `InvalidOperationException`.

The client provides:

- Database: `ListDatabasesAsync`, `CreateDatabaseAsync`, `GetDatabaseInfoAsync`, `GetHealthAsync`, `GetCapabilitiesAsync`, and `GetHeadAsync`
- Catalog: schema and handler registration, `GetSchemaAsync`, `ListSchemasAsync`, `RemoveSchemaAsync`, `RemoveHandlerAsync`, `GetHandlerAsync`, `ListHandlersAsync`, decision registration, and `ValidateNdlAsync`
- Command: `ExecuteHandlerAsync<TCommand>` and `GetEventsByCommandIdAsync`
- Event: `ReadEventsByRangeAsync`, `ReadEventsByQueryAsync`, `ReadEventsByTypeAndKeysAsync`, and `SubscribeEventsAsync` as `IAsyncEnumerable<SequencedEvent>`
- Statement: `ExecuteStatementAsync` as `IAsyncEnumerable<StatementResult>` and `ExplainStatementAsync`
- Administration: `ListPartitionsAsync`, `ListIndexesAsync`, `GetStateFileStatusAsync`, `RequestIndexRebuildAsync`, and `RequestStateRebuildAsync`
- public static request builders plus `MapQuery` and `MapEvent`

```csharp
ExecuteHandlerResponse response = await client.ExecuteHandlerAsync(
    "school",
    "SubscribeStudentSdk",
    new SubscribeStudentToCourse("student-1", "course-1"),
    commandId: Guid.NewGuid(),
    cancellationToken);

await foreach (SequencedEvent item in client.ReadEventsByQueryAsync(
    "school",
    new EventQuery([
        new QueryItem(
            ["StudentSubscribedToCourse"],
            [new EventKey("student", "student-1")])
    ]),
    cancellationToken: cancellationToken))
{
    Console.WriteLine($"{item.EventId}: {item.Type}");
}
```

Unary operations and statement streams expose generated protocol response/result types rather than a custom typed result union; event streams map envelopes to model `SequencedEvent` values. The client passes cancellation but does not configure default deadlines or retries.

## SDK Runtime Diagnostics

Building a fluent definition can report:

| Code | Meaning |
|---|---|
| `NDCB100` | Event schema descriptor could not be created. |
| `NDCB101` | `Where` contains something other than equality/conditional-and. |
| `NDCB102` | `Where` binds no equality. |
| `NDCB103` | Equality has no direct event property side. |
| `NDCB104` | Direct event property is not a consistency key. |
| `NDCB105` | Key value depends on the event payload. |
| `NDCB106` | Captured key value cannot be evaluated/encoded. |
| `NDCB107` | Same logical key is bound more than once. |

`Compile` refuses definitions containing an error diagnostic, then may throw for unsupported later expression shapes.

## Roslyn Analyzer

`SchemaAnalyzer` analyzes `[EventType]` declarations only:

| Code | Meaning |
|---|---|
| `NDCB001` | Event has no `[ConsistencyKey]` property. |
| `NDCB002` | Stable event schema name appears on multiple types in one compilation. |
| `NDCB003` | Logical key name is duplicated on one event type. |
| `NDCB004` | Key property type is unsupported. |

Supported analyzer key types match the runtime set except nullable forms are unwrapped. Command declarations, fluent ordering/expressions, determinism, event construction, and schema-name whitespace are not analyzed today.

## Source Generator

`SchemaGenerator` discovers `[EventType]` and `[CommandType]` types and emits one deterministic factory:

```csharp
NativeDCB.Generated.NativeDcbGeneratedSchemas.Create()
```

It returns ordered `SchemaDescriptor.ForEvent<T>()` / `ForCommand<T>()` calls. It emits no diagnostics and does not currently generate serializer contexts, fingerprints, tag encoders, registration methods, plans, or Orleans adapters. Descriptor work remains reflection-based at runtime.

## Sample And Tests

`samples/CourseSubscriptions` is analyzer-wired through direct project analyzer references and invokes `NativeDCB.Generated.NativeDcbGeneratedSchemas.Create()`. Its idempotent `seed` mode creates the database and registers all event/command schemas plus both NDL handlers and the fluent SDK handler without emitting events. Its `run` mode invokes the same seeder before defining a course and executing subscriptions through both authoring paths.

SDK tests cover descriptors/tags, fluent type-state/order, query translation, plan shape, request mapping, analyzer diagnostics, and generator output. Server integration includes a full SDK flow across Database, Catalog, Statement, Command, Event, and Administration services. The end-to-end project retains an in-process authoring-equivalence test and also launches a real server process with isolated ports and temporary storage to run the exact sample NDL alongside an SDK-authored plan, execute commands, subscribe, and verify streamed and persisted events.
