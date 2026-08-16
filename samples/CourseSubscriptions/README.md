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

`NativeDCB.EndToEndTests.CourseSubscriptionsProcessTests` launches a real server process and this sample's `seed` mode with isolated ports and temporary storage. It seeds twice, verifies the exact schema/handler catalog at head zero, and then confirms that the NDL and SDK paths produce the expected streamed and persisted events.
