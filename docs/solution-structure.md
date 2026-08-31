# NativeDCB Solution Structure

Status: implementation reference with future work called out explicitly  
Last verified: 2026-08-22

## Repository

NativeDCB is a .NET 10 solution (`NativeDCB.slnx`) containing eleven source projects, seven test projects, one sample application, one reusable sample domain library, and two benchmark executables. Package versions are declared directly in project files; there is no `Directory.Packages.props`. Common nullable, analyzer, deterministic-build, and warnings-as-errors settings are in `Directory.Build.props`. `global.json` selects SDK `10.0.400` with latest-patch roll-forward.

```text
src/
  NativeDCB.Model
  NativeDCB.Protocol
  NativeDCB.Ndl
  NativeDCB.Engine
  NativeDCB.Actors
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
  Commerce/
    NativeDCB.Commerce
    NativeDCB.Commerce.Sample
benchmarks/
  NativeDCB.MicroBenchmarks
  NativeDCB.SystemBenchmarks
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

`Protos/v1/nativedcb.proto` is the single versioned protobuf source. The build generates both clients and server bases in `NativeDCB.Protocol.V1`. It contains all eight public services and 35 RPCs; CommandService has four decision/reconciliation methods, AuthenticationService manages API keys, and AuditService lists durable audit records. Generated C# is build output.

### `NativeDCB.Ndl`

The NDL library implements source text and spans, lexing, parsing with recovery diagnostics, syntax records, canonical formatting, compilation to `NativeDCB.Model.DecisionPlan`, and diagnostic-bearing conversion of representable plans back to canonical NDL. It references only `NativeDCB.Model`. Schema-aware semantic validation and execution live in `NativeDCB.Actors`, not in this library.

Its physical folders separate `Text`, `Diagnostics`, `Lexing`, `Parsing`, `Syntax`, `Compilation`, and `Formatting`. Syntax expressions and statements are nested beneath `Syntax`.

### `NativeDCB.Engine`

The Orleans-free engine implements JSON/NDJSON event-log storage, the partitioned hash-chained audit journal, partition discovery and reads, checkpoint-assisted recovery with full authoritative fallback, state-file construction/inspection, manifest/generation index persistence, and BCL telemetry instruments. It references only `NativeDCB.Model`; `NativeDCB.Actors` and the server have internal access to required storage helpers.

Storage is grouped into `Audit`, `EventLog`, `Indexes`, and `State`. The engine does not host gRPC, define grains, own catalogs, or execute NDL decisions.

### `NativeDCB.Actors`

`NativeDCB.Actors` is the Orleans application layer. It owns actor contracts/messages and implementations for Database Directory, Main Writer, Schema, Handler, Decision, Audit, stateless event-log reads, index mutation/replicas/orchestration, State/State Builder/State Orchestrator, subscriptions, and remote-decision routing. It also contains sanitized audit-context propagation, BCL telemetry, catalog/schema validation, plan execution, HMAC capability handling, actor mapping, and storage-path configuration.

Files are grouped under `Contracts`, `Messages`, `Grains`, `Catalog`, `Decisions`, `Mapping`, and `Storage`. Actor namespaces use the `NativeDCB.Actors.*` root. The actor project references Engine, Model, NDL, and Orleans.

### `NativeDCB.Server`

The ASP.NET Core server hosts an Orleans localhost cluster and all generated gRPC service implementations. It maps protocol messages to actor messages, propagates cancellation, maps actor results, bridges streaming responses, and exposes HTTP liveness/readiness endpoints. All 31 database-operation RPCs and the audit RPC enter actors; the three authentication RPCs manage the server-owned API-key catalog. All eight services support native HTTP/2 gRPC and gRPC-Web; cross-origin browser access is restricted by `GrpcWeb:AllowedOrigins`.

The server security layer validates OIDC/JWTs and generated API keys, authorizes every gRPC action, enforces handler/audit resource permissions, records denials and pre-operation audit attempts, and owns bootstrap/recovery and the versioned API-key store under `Security`. Server-only OpenTelemetry packages export logs, traces, and metrics through OTLP when configured.

The server has no Transaction grain, database registry, catalog store, or direct event/index/state storage path. There are no generic public-service facade actors: each gRPC method calls the actor responsible for the operation, and domain actors orchestrate workflows that span owners. See [Actor Architecture](actor-architecture.md).

### `NativeDCB.Sdk`

The runtime SDK provides schema attributes and reflection descriptors, JSON Schema generation, consistency-key encoding, the typed fluent decision builder and plan compiler, typed prepared-model and proposed-event helpers, request builders/mappers, refresh-capable bearer/API-key call credentials, and a gRPC client wrapping every RPC across all eight services. Its NuGet package embeds the analyzer and generator DLLs as standard C# analyzer assets.

SDK files are grouped physically under `Schemas`, `Decisions/Authoring`, `Decisions/Compilation`, `Decisions/Diagnostics`, and `Client`.

### `NativeDCB.Cli`

The `nativedcb` console application is a native gRPC client for all 35 RPCs. It supports access-token/API-key files and environment variables, API-key management, paginated audit queries, JSON and NDL from command arguments, files, or standard input, remote prepare/complete inputs, query/key convenience inputs, JSON/JSONL output, streaming cancellation, and stable process exit codes. It defaults to the native HTTP/2 endpoint `http://localhost:5010`. See [CLI](cli.md).

CLI implementation files are grouped under `Application`, `Arguments`, `IO`, and `Presentation`, with `Program.cs` remaining at the project root.

### `NativeDCB.Sdk.Analyzers` and `NativeDCB.Sdk.Generators`

These are separate `netstandard2.0` Roslyn projects. Each packs its DLL under `analyzers/dotnet/cs`; the `NativeDCB.Sdk` package also embeds both DLLs there. `samples/Commerce/NativeDCB.Commerce` uses direct analyzer project references for repository builds and consumes the generated schema factory.

- The analyzer reports four schema diagnostics (`NDCB001`-`NDCB004`).
- The incremental generator emits `NativeDCB.Generated.NativeDcbGeneratedSchemas.Create()`.

### `NativeDCB.Web`

The web console is a standalone Blazor WebAssembly application. It references only the public protocol, runs inside the browser's WebAssembly runtime boundary, and calls the NativeDCB server directly over gRPC-Web. There is no server-side application host or backend-for-frontend in this project; the development host only serves the static WebAssembly assets.

Its single-page RPC workbench has explicit controls for all 35 methods across the eight services, including API-key management, paginated audit reads, and remote decision preparation/completion. Navigation, method counts, request/response type metadata, and missing-wrapper detection are derived from generated protobuf descriptors; domain-heavy forms remain hand-authored. The prominent NDL editor drives validation, explanation, and streamed statement execution. Event reads, follow mode, subscriptions, and statement execution render stream items incrementally, and the active unary call or stream can be cancelled.

The browser gRPC facade is under `Grpc`, descriptor discovery is nested under `Grpc/Discovery`, and Razor components retain their existing `Components/Layout` and `Components/Pages` grouping.

`wwwroot/appsettings.json` is public browser configuration and defaults `NativeDCB:ServerAddress` to `https://localhost:7154`. Browser origins must also be present in the server's `GrpcWeb:AllowedOrigins`; the defaults cover the Web project's HTTP and HTTPS launch origins. This CORS allowlist does not provide authentication. The application uses OIDC authorization-code flow with PKCE and does not persist operator history, consume external logs, store credentials in static configuration, or access database files directly.

### Benchmark Executables

`NativeDCB.MicroBenchmarks` uses BenchmarkDotNet for eight deterministic CPU/allocation classes and five isolated filesystem classes covering queries, schemas, mapping, NDL/SDK compilation, pure decision/index kernels, append, recovery, partition reads, index persistence, and state generation. Filesystem runs require an explicit `NATIVEDCB_BENCHMARK_ROOT` and report the selected storage environment.

`NativeDCB.SystemBenchmarks` uses NBomber plus a repository-owned coordinator. It launches a real Release server with fresh ports and storage under explicit disabled-authentication test configuration, seeds Native Commerce, runs one of 12 independently selectable scenarios through closed-loop, planned open-loop, synchronized-burst, or correlated-workflow scheduling, verifies persisted history, samples the child process, and writes a versioned result bundle. Optional profiling attaches `dotnet-counters`, `dotnet-trace`, or `dotnet-gcdump` to the server PID.

## Dependency Direction

```text
Ndl -> Model
Engine -> Model
Actors -> Engine + Model + Ndl
Server -> Actors + Engine + Model + Ndl + Protocol
Sdk -> Model + Protocol
Cli -> Model + Protocol + Sdk
Web -> Protocol
MicroBenchmarks -> Actors + Engine + Model + Ndl + Sdk + Commerce
SystemBenchmarks -> Sdk + Protocol + Commerce; launches Server as a child process

Sdk.Analyzers and Sdk.Generators are Roslyn build tools referenced privately for SDK packaging and directly as analyzers by NativeDCB.Commerce.
```

The Server project still has direct project references to Engine, Model, and NDL in addition to Actors and Protocol, although `NativeDCB.Server/Grpc` uses actor contracts/messages and public model mapping rather than Engine storage, catalogs, or decision runtimes. The SDK, CLI, and Web application do not reference Orleans or Engine. Web does not reference the SDK and uses generated protocol clients directly.

## Runtime Boundaries

The runnable boundaries are:

- `NativeDCB.Server`: owns the local Orleans silo and gRPC/health endpoints; hosted actors own database workflows and file mutation.
- `NativeDCB.Cli`: a short- or long-lived native HTTP/2 gRPC client process.
- `NativeDCB.Web`: static assets served by the development host or another static host, with application code executing in the browser WebAssembly runtime and calling `NativeDCB.Server` directly over gRPC-Web.
- `NativeDCB.MicroBenchmarks`: short-lived BenchmarkDotNet hosts for deterministic CPU and environment-sensitive filesystem measurements.
- `NativeDCB.SystemBenchmarks`: coordinator/load-generator process that owns a fresh child server, result bundle, and correctness boundary for each independent run.

`NativeDCB.Server` remains the executable host, but the actor layer owns database state and file mutation. The server transport layer maps requests to actor messages and does not orchestrate database operations.

There is no Web BFF or proxy boundary. External .NET applications can use `NativeDCB.Sdk`. NDL source and SDK expression trees independently compile to the same `DecisionPlan` model. SDK plans are serialized directly into `RegisterHandlerRequest.plan_json`; they are not translated into NDL.

## Tests And Sample

- NDL tests cover lexer/parser diagnostics, formatting, and plan compilation.
- Engine tests cover durable NDJSON, query/append semantics, writer locking, partition rollover, recovery/truncation/corruption, state checkpoint construction/restore/validation/fallback, and equivalence coverage for the pure decision-model and index-combination benchmark seams.
- SDK tests cover attributes/descriptors, deterministic tags, fluent type states, query translation, plan compilation, and protocol mapping.
- Analyzer/generator tests cover current diagnostics and deterministic factory generation.
- Server integration tests start the ASP.NET/Orleans host with a temporary root and cover database creation, catalog persistence, commands, retries, reads/follow, schemas, indexes, state files, lock failure with snapshot-read availability, multi-decision NDL statements, SDK plans, remote prepare/complete behavior, restart, a full SDK flow, audit permissions/outcomes/corruption, and bounded telemetry dimensions. Authentication coverage exercises real JWT/API-key validation, wildcard and handler/audit permissions, delegation, bootstrap/recovery, SDK propagation, and the 35-method gRPC-Web catalog. Native Commerce coverage adds deterministic fixtures, idempotent catalog seeding, a complete ordered lifecycle, contention, duplicate reconciliation, typed remote payment completion, stale capabilities, and durable restart.
- The end-to-end project has an in-process authoring-equivalence test and a Commerce process E2E test, `CommerceSampleProcessTests`, that launches the built server on isolated ports with a temporary database root. The process test runs the Commerce sample's idempotent seeder twice, verifies the complete catalog at head zero, subscribes, executes local and remote decisions, verifies streamed/persisted events, queries audit records, restarts the built server, and verifies audit sequence/hash continuity.
- `samples/Commerce/NativeDCB.Commerce` provides the 21 command and 26 event contracts, generated schemas, the 21 decisions in `NativeCommerce.ndl`, three fluent SDK decisions, typed preparation models, deterministic fixtures, and the idempotent catalog seeder for Native Commerce. It is the repository consumer of both Roslyn analyzer projects and the generated schema factory.
- `samples/Commerce/NativeDCB.Commerce.Sample` is the sole sample application and exposes `seed`, `run`, `contention`, `remote-payment`, and `recovery` modes. Every mode seeds first; the scenarios execute a complete commerce lifecycle, verify domain invariants under contention, demonstrate trusted remote payment completion, and reconcile duplicate commands.
- Benchmark executables are normal non-test projects. They build with the solution, are not discovered by `dotnet test`, and run only when invoked explicitly.

The Commerce sample keeps its executable host at the application project root and `NativeCommerce.ndl` at the reusable domain project root. Domain types are grouped physically into `Commands`, `Events`, `Models`, and `Fixtures`.

All current tests use xUnit and temporary directories where storage is involved.

Test files mirror the corresponding physical production areas where useful, while retaining their existing project-level namespaces. The physical folder cleanup intentionally leaves namespace migration to a separate ReSharper refactor.

## Not Yet Implemented

The current structure does not contain multi-silo storage coordination, backup/restore, audit retention/archival, event compaction/retention, grouped write buffering, model-only plans, or package publishing/release infrastructure. Packages can be built locally, but no automated publication or signed release pipeline is configured.

See [Requirements](requirements.md) for the requirement status matrix and [Database Lifecycle](database-lifecycle.md) for exact operational behavior.
