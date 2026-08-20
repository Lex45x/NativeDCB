# NativeDCB

NativeDCB is an experimental .NET 10 Dynamic Consistency Boundaries event store. It combines an Orleans actor application layer in `NativeDCB.Actors`, a single-writer decision runtime, a durable JSON/NDJSON event log, a versioned gRPC and gRPC-Web API, NDL and fluent C# decision authoring, optional two-step remote decisions, a native CLI, and a standalone Blazor WebAssembly operator console. All 31 RPCs route database work through actors; the ASP.NET Core server maps transport messages and bridges streams.

The implementation is usable but not production complete. Writer startup can recover from a validated cumulative state checkpoint and replay only later partitions, with authoritative full-log recovery when no checkpoint is valid. Decision-model hydration combines derived index snapshots with an authoritative committed tail. The server remains single-silo and has no caller authentication/authorization, audit, or backup facilities. See [Requirements](docs/requirements.md) for an honest status matrix.

## Prerequisites

- .NET SDK 10.0.400 (selected by `global.json`)
- A trusted ASP.NET Core HTTPS development certificate for the browser console (`dotnet dev-certs https --trust`)

## Build And Test

```powershell
dotnet restore NativeDCB.slnx
dotnet build NativeDCB.slnx --no-restore
dotnet test NativeDCB.slnx --no-build
```

The solution includes NDL, engine, SDK, analyzer/generator, server integration, process-level end-to-end, and authoring-equivalence tests. Coverage includes a full SDK flow across all six protocol services and browser-style gRPC-Web unary and server-streaming integration.

## Run

Start the server with its `https` profile. Setting `DatabaseRoot` keeps this quickstart's data in a disposable location rather than the default `%LOCALAPPDATA%\NativeDCB\databases`.

```powershell
$env:DatabaseRoot = "$env:TEMP\NativeDCB-quickstart"
dotnet run --project src\NativeDCB.Server --launch-profile https
```

This profile exposes native gRPC over HTTP/2 at `http://localhost:5010` for the CLI, SDK, and sample, and an HTTPS endpoint at `https://localhost:7154` used by the browser over gRPC-Web. It also exposes `GET /health/live` and `GET /health/ready`.

Remote decision preparation/completion is disabled unless the server has an HMAC key under `RemoteDecisions`. See [Database Lifecycle](docs/database-lifecycle.md#remote-decisions) for configuration, rotation, restart, and bearer-capability guidance.

In another shell, create the database with the repository CLI:

```powershell
dotnet run --project src\NativeDCB.Cli -- database create --database school
```

The CLI defaults to `http://localhost:5010` and covers all 31 RPCs. See the [CLI reference](docs/cli.md) for schemas, NDL statements, commands, remote decision preparation/completion, reads, subscriptions, input conventions, and exit codes.

Start the standalone WebAssembly console with its `https` profile:

```powershell
dotnet run --project src\NativeDCB.Web --launch-profile https
```

Open `https://localhost:7229`. The browser loads a standalone Blazor WebAssembly application and calls `https://localhost:7154` directly with gRPC-Web; there is no Web backend-for-frontend. The workbench provides explicit controls for all 31 RPCs, incremental streaming and cancellation, and a prominent NDL editor shared by validation, explanation, and execution. Service navigation, method counts, and wrapper coverage are derived from protobuf descriptors.

The browser endpoint is configured in public static content at `src/NativeDCB.Web/wwwroot/appsettings.json`; never put credentials or secrets there. Cross-origin browser calls are allowed only from origins in the server's `GrpcWeb:AllowedOrigins` configuration, which defaults to the two local Web launch origins. CORS is not authentication or authorization. NativeDCB currently provides neither, so do not expose the server or console to untrusted networks.

## Course Subscriptions Sample

After the server is running:

```powershell
dotnet run --project samples\CourseSubscriptions -- run http://localhost:5010 school
```

Use `seed` instead of `run` to idempotently create the database and register all schemas and handlers without publishing events. Run mode seeds first, then defines a course and executes one NDL and one SDK subscription. Both authoring paths converge on `DecisionPlan` and the same transaction runtime.

Use `remote` to seed, define a course, hydrate the SDK handler's typed model with `PrepareDecision`, and submit the event with `CompleteDecision`. Configure `RemoteDecisions` before starting the server for this mode.

```powershell
dotnet run --project samples\CourseSubscriptions -- remote http://localhost:5010 school
```

## Documentation

- [Requirements and implementation status](docs/requirements.md)
- [Implemented actor architecture and persistence ownership](docs/actor-architecture.md)
- [Solution structure, projects, tests, and sample](docs/solution-structure.md)
- [Internal engine, actors, NDJSON, state files, and indexes](docs/internal-engine.md)
- [Database lifecycle and health](docs/database-lifecycle.md)
- [Complete gRPC API and errors](docs/grpc-api.md)
- [CLI commands, inputs, output, and examples](docs/cli.md)
- [NativeDCB Decision Language](docs/dsl.md)
- [.NET SDK, plan APIs, analyzer, and generator](docs/dotnet-sdk.md)
