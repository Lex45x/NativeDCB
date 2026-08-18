# Course Subscriptions

This sample can seed a database or execute equivalent course-subscription decisions through NDL and the fluent .NET SDK.

Start `NativeDCB.Server`, then run:

```powershell
dotnet run --project samples\CourseSubscriptions -- seed http://localhost:5010 school
```

`seed` is idempotent. It creates the database when absent, registers all four event/command schemas, registers the two decisions in `CourseSubscriptions.ndl`, registers `SubscribeStudentSdk`, and exits without publishing events.

Use `run` to seed first and then execute the sample scenario:

```powershell
dotnet run --project samples\CourseSubscriptions -- run http://localhost:5010 school
```

The run mode defines one course and executes one subscription through each authoring path. Both subscription handlers compile to the shared storage-neutral `DecisionPlan` and execute through the same Orleans transaction runtime.

## Remote decisions

Remote decision preparation and completion use an HMAC key configured on the server. Set an active key ID and a base64-encoded key of at least 32 bytes before starting `NativeDCB.Server`:

```powershell
$env:RemoteDecisions__ActiveKeyId = "dev"
$env:RemoteDecisions__SigningKeys__dev = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
dotnet run --project src\NativeDCB.Server
```

The environment variable suffix (`dev` above) must match the active key ID. Use a securely generated key outside local development.

The `remote` mode seeds the catalog, defines the sample course, prepares the registered `SubscribeStudentSdk` model through `NativeDcbClient`, evaluates the returned `CourseModel` locally, and completes the decision with a `StudentSubscribedToCourse` event:

```powershell
dotnet run --project samples\CourseSubscriptions -- remote http://localhost:5010 school
```

It prints the completion outcome as `Remote SubscribeStudentSdk: Committed` when successful.

`NativeDCB.EndToEndTests.CourseSubscriptionsProcessTests` launches a real server process and this sample's `seed` mode with isolated ports, deterministic HMAC configuration, and temporary storage. It seeds twice, verifies the exact schema/handler catalog at head zero, and then confirms that the NDL, SDK, and remote SDK paths produce the expected streamed and persisted events, including idempotent remote replay.
