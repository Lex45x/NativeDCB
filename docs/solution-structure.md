# NativeDCB Solution Structure

Status: implementation reference with future work called out explicitly  
Last verified: 2026-08-18

## Repository

NativeDCB is a .NET 10 solution (`NativeDCB.slnx`) containing ten source projects, seven test projects, and one sample. Package versions are declared directly in project files; there is no `Directory.Packages.props`. Common nullable, analyzer, deterministic-build, and warnings-as-errors settings are in `Directory.Build.props`. `global.json` selects SDK `10.0.400` with latest-patch roll-forward.

```text
src/
  NativeDCB.Model
  NativeDCB.Protocol
  NativeDCB.Ndl
  NativeDCB.Engine
  NativeDCB.Server
  NativeDCB.Cli
  NativeDCB.Sdk
  NativeDCB.Sdk.Analyzers
  NativeDCB.Sdk.Generators
  NativeDCB.Web
tests/
  NativeDCB.Ndl.Tests
  NativeDCB.Engine.Tests
  NativeDCB.Server.IntegrationTests
  NativeDCB.Sdk.Tests
  NativeDCB.Sdk.Analyzers.Tests
  NativeDCB.Sdk.Generators.Tests
  NativeDCB.EndToEndTests
samples/
  CourseSubscriptions
```

There is no `NativeDCB.Web.Tests` project and no container, CI, signing, package-publishing, or release configuration in the repository. NuGet packing is configured for the SDK, analyzer, and generator projects.

## Project Responsibilities

### `NativeDCB.Model`

The storage-neutral contract library defines:

- `EventKey`, candidate and sequenced events, batches, queries, append conditions, and append results
- `DecisionPlan`, keyed includes, evaluation steps, emissions, expression nodes, and fingerprints
- database status and handler-registration records

It has no Orleans, protobuf, filesystem, NDL, SDK, or web dependency.

Files are grouped physically under `Catalog`, `Databases`, `Events`, `Queries`, and `Decisions`; decision evaluation and expression nodes have dedicated nested folders.

### `NativeDCB.Protocol`

`Protos/v1/nativedcb.proto` is the single versioned protobuf source. The build generates both clients and server bases in `NativeDCB.Protocol.V1`. It contains all six public services and 31 RPCs; CommandService has `ExecuteHandler`, `PrepareDecision`, `CompleteDecision`, and `GetEventsByCommandId`. Generated C# is build output.

### `NativeDCB.Ndl`

The NDL library implements source text and spans, lexing, parsing with recovery diagnostics, syntax records, canonical formatting, compilation to `NativeDCB.Model.DecisionPlan`, and diagnostic-bearing conversion of representable plans back to canonical NDL. It references only `NativeDCB.Model`. Schema-aware semantic validation and execution live in the server, not in this library.

Its physical folders separate `Text`, `Diagnostics`, `Lexing`, `Parsing`, `Syntax`, `Compilation`, and `Formatting`. Syntax expressions and statements are nested beneath `Syntax`.

### `NativeDCB.Engine`

The engine implements JSON/NDJSON persistence and Orleans grain contracts/implementations for:

- one Main Writer grain per database name
- one Read grain per database name
- one State Builder grain per database name
- one Index Coordinator grain per database and one Index grain per `(event type, key name, key value)` identity
- partition discovery, checkpoint-assisted recovery with full authoritative fallback, state-file construction/inspection, and derived index persistence

The engine references `NativeDCB.Model` and Orleans. It does not host gRPC or execute NDL decisions.

Actor files are physically separated into `Contracts`, `Messages`, `Mapping`, and `Grains`. Storage is grouped into `EventLog`, `Indexes`, and `State`.

### `NativeDCB.Server`

The ASP.NET Core server hosts an Orleans localhost cluster, all generated gRPC service implementations, registry/catalog storage, schema validation, the Transaction grain, the NDL/plan runtime, and HMAC protection for optional stateless remote-decision capabilities. It maps protocol messages to model/actor messages and exposes HTTP liveness/readiness endpoints. All six services support native HTTP/2 gRPC and gRPC-Web; cross-origin browser access is restricted by `GrpcWeb:AllowedOrigins`.

The Transaction grain is in the server because it executes captured handler plans and depends on server catalog/schema types. See [Internal Engine](internal-engine.md).

Server files are grouped physically by `Grpc`, `Databases`, `Catalog`, and `Decisions`; gRPC infrastructure, schema handling, execution, transactions, and remote-decision token handling use nested folders.

### `NativeDCB.Sdk`

The runtime SDK provides schema attributes and reflection descriptors, JSON Schema generation, consistency-key encoding, the typed fluent decision builder and plan compiler, typed prepared-model and proposed-event helpers, request builders/mappers, and a gRPC client wrapping every RPC across Database, Catalog, Command, Event, Statement, and Administration services. Its NuGet package embeds the analyzer and generator DLLs as standard C# analyzer assets.

SDK files are grouped physically under `Schemas`, `Decisions/Authoring`, `Decisions/Compilation`, `Decisions/Diagnostics`, and `Client`.

### `NativeDCB.Cli`

The `nativedcb` console application is a native gRPC client for all 31 RPCs. It supports JSON and NDL from command arguments, files, or standard input; remote prepare/complete inputs; query/key convenience inputs; JSON/JSONL output; streaming cancellation; and stable process exit codes. It defaults to the native HTTP/2 endpoint `http://localhost:5010`. See [CLI](cli.md).

CLI implementation files are grouped under `Application`, `Arguments`, `IO`, and `Presentation`, with `Program.cs` remaining at the project root.

### `NativeDCB.Sdk.Analyzers` and `NativeDCB.Sdk.Generators`

These are separate `netstandard2.0` Roslyn projects. Each packs its DLL under `analyzers/dotnet/cs`; the `NativeDCB.Sdk` package also embeds both DLLs there. `samples/CourseSubscriptions` uses direct analyzer project references for repository builds and consumes the generated schema factory.

- The analyzer reports four schema diagnostics (`NDCB001`-`NDCB004`).
- The incremental generator emits `NativeDCB.Generated.NativeDcbGeneratedSchemas.Create()`.

### `NativeDCB.Web`

The web console is a standalone Blazor WebAssembly application. It references only the public protocol, runs inside the browser's WebAssembly runtime boundary, and calls the NativeDCB server directly over gRPC-Web. There is no server-side application host or backend-for-frontend in this project; the development host only serves the static WebAssembly assets.

Its single-page RPC workbench has explicit controls for all 31 methods across the six services, including remote decision preparation and completion. Navigation, method counts, request/response type metadata, and missing-wrapper detection are derived from generated protobuf descriptors; domain-heavy forms remain hand-authored. The prominent NDL editor drives validation, explanation, and streamed statement execution. Event reads, follow mode, subscriptions, and statement execution render stream items incrementally, and the active unary call or stream can be cancelled.

The browser gRPC facade is under `Grpc`, descriptor discovery is nested under `Grpc/Discovery`, and Razor components retain their existing `Components/Layout` and `Components/Pages` grouping.

`wwwroot/appsettings.json` is public browser configuration and defaults `NativeDCB:ServerAddress` to `https://localhost:7154`. Browser origins must also be present in the server's `GrpcWeb:AllowedOrigins`; the defaults cover the Web project's HTTP and HTTPS launch origins. This CORS allowlist does not provide authentication. The application does not persist operator history, consume external logs, authenticate users, or access database files directly.

## Dependency Direction

```text
Ndl -> Model
Engine -> Model
Server -> Engine + Model + Ndl + Protocol
Sdk -> Model + Protocol
Cli -> Model + Protocol + Sdk
Web -> Protocol

Sdk.Analyzers and Sdk.Generators are Roslyn build tools referenced privately for SDK packaging and directly as analyzers by the sample.
```

The SDK, CLI, and Web application do not reference Orleans or the engine. Web does not reference the SDK and uses generated protocol clients directly.

## Runtime Boundaries

The runnable boundaries are:

- `NativeDCB.Server`: owns the local Orleans silo, database registry, files, and gRPC/health endpoints.
- `NativeDCB.Cli`: a short- or long-lived native HTTP/2 gRPC client process.
- `NativeDCB.Web`: static assets served by the development host or another static host, with application code executing in the browser WebAssembly runtime and calling `NativeDCB.Server` directly over gRPC-Web.

There is no Web BFF or proxy boundary. External .NET applications can use `NativeDCB.Sdk`. NDL source and SDK expression trees independently compile to the same `DecisionPlan` model. SDK plans are serialized directly into `RegisterHandlerRequest.plan_json`; they are not translated into NDL.

## Tests And Sample

- NDL tests cover lexer/parser diagnostics, formatting, and plan compilation.
- Engine tests cover durable NDJSON, query/append semantics, writer locking, partition rollover, recovery/truncation/corruption, and state checkpoint construction, restore, validation, and fallback.
- SDK tests cover attributes/descriptors, deterministic tags, fluent type states, query translation, plan compilation, and protocol mapping.
- Analyzer/generator tests cover current diagnostics and deterministic factory generation.
- Server integration tests start the ASP.NET/Orleans host with a temporary root and cover database creation, catalog persistence, commands, retries, reads/follow, schemas, indexes, state files, lock failure with snapshot-read availability, multi-decision NDL statements, SDK plans, remote prepare/complete behavior, restart, and a full SDK flow across all six protocol services. A dedicated gRPC-Web integration test exercises browser-style unary and server-streaming calls through the configured CORS origin.
- The end-to-end project has an in-process authoring-equivalence test and a process-level test that launches the built server on isolated ports with a temporary database root. The process test loads the exact sample `CourseSubscriptions.ndl`, registers generated schemas and NDL/SDK handlers, subscribes, executes local and remote commands, replays remote completion, and verifies streamed and persisted events through the real transport and storage path.
- `samples/CourseSubscriptions` runs both analyzer projects and exposes explicit `seed`, `run`, and `remote` modes. Seed mode idempotently creates the database and registers all four schemas plus both NDL handlers and the fluent SDK handler without publishing events. Run mode seeds first and executes the local course/subscription scenario. Remote mode seeds, defines a course, prepares the SDK handler model, evaluates that model in the client, and completes with a proposed event. The process-level end-to-end test launches seed mode twice, verifies the catalog at head zero, and then verifies local and remote live/persisted events plus idempotent remote replay.

The sample keeps its host and NDL document at the project root and groups domain types physically into `Commands`, `Events`, and `Models`.

All current tests use xUnit and temporary directories where storage is involved.

Test files mirror the corresponding physical production areas where useful, while retaining their existing project-level namespaces. The physical folder cleanup intentionally leaves namespace migration to a separate ReSharper refactor.

## Not Yet Implemented

The current structure does not contain authentication/authorization, audit persistence, metrics/tracing setup, multi-silo storage coordination, backup/restore, compaction/retention, grouped write buffering, model-only plans, or package publishing/release infrastructure. Packages can be built locally, but no automated publication or signed release pipeline is configured.

See [Requirements](requirements.md) for the requirement status matrix and [Database Lifecycle](database-lifecycle.md) for exact operational behavior.
