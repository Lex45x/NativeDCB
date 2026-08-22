# NativeDCB

NativeDCB is an experimental .NET 10 Dynamic Consistency Boundaries event store. It combines an Orleans actor application layer in `NativeDCB.Actors`, a single-writer decision runtime, a durable JSON/NDJSON event log, a versioned gRPC and gRPC-Web API, NDL and fluent C# decision authoring, optional two-step remote decisions, a native CLI, and a standalone Blazor WebAssembly operator console. The 31 database RPCs route database work through actors; three authentication RPCs manage server-generated API keys.

The implementation is usable but not production complete. Writer startup can recover from a validated cumulative state checkpoint and replay only later partitions, with authoritative full-log recovery when no checkpoint is valid. Decision-model hydration combines derived index snapshots with an authoritative committed tail. The server authenticates OIDC/JWT or generated API-key callers and enforces per-RPC and per-handler permissions, but remains single-silo and has no audit or backup facilities. See [Requirements](docs/requirements.md) for an honest status matrix.

## Prerequisites

- .NET SDK 10.0.400 (selected by `global.json`)
- A trusted ASP.NET Core HTTPS development certificate for the browser console (`dotnet dev-certs https --trust`)

## Build And Test

```powershell
dotnet restore NativeDCB.slnx
dotnet build NativeDCB.slnx --no-restore
dotnet test NativeDCB.slnx --no-build
```

The solution includes NDL, engine, SDK, analyzer/generator, server integration, process-level end-to-end, and authoring-equivalence tests. Coverage includes a full SDK flow across all seven protocol services, real JWT/API-key authorization, and browser-style gRPC-Web unary and server-streaming integration.

## Run

Start the server with its `https` profile. Setting `DatabaseRoot` keeps this quickstart's data in a disposable location rather than the default `%LOCALAPPDATA%\NativeDCB\databases`.

```powershell
$env:DatabaseRoot = "$env:TEMP\NativeDCB-quickstart"
dotnet run --project src\NativeDCB.Server --launch-profile https
```

This profile exposes native gRPC over HTTP/2 at `http://localhost:5010` for the CLI, SDK, and sample, and an HTTPS endpoint at `https://localhost:7154` used by the browser over gRPC-Web. It also exposes `GET /health/live` and `GET /health/ready`.

API-key authentication is enabled by default. On an empty key store, startup prints one random bootstrap key with `*:*:*`; readiness remains false until it is replaced. In another shell, use that key once to create the normal local administrator key:

```powershell
$env:NATIVEDCB_API_KEY = "<bootstrap key printed by the server>"
dotnet run --project src\NativeDCB.Cli -- --server https://localhost:7154 `
  auth create-api-key --label local-admin --permission "*:*:*" > .\local-admin.json
$env:NATIVEDCB_API_KEY = (Get-Content -Raw .\local-admin.json | ConvertFrom-Json).apiKey
```

Protect and delete `local-admin.json` after installing the key in an appropriate secret store. Creating the first normal key atomically revokes the bootstrap key. See [Authentication And Authorization](docs/authentication-authorization.md) for OIDC, key recovery, rotation, and scope semantics.

Remote decision preparation/completion is disabled unless the server has an HMAC key under `RemoteDecisions`. See [Database Lifecycle](docs/database-lifecycle.md#remote-decisions) for configuration, rotation, restart, and bearer-capability guidance.

In another shell, create the database with the repository CLI:

```powershell
dotnet run --project src\NativeDCB.Cli -- --server https://localhost:7154 database create --database quickstart
```

The CLI covers all 34 RPCs and adds JWT/API-key credentials to each call. See the [CLI reference](docs/cli.md) for authentication, schemas, NDL statements, commands, remote decision preparation/completion, reads, subscriptions, input conventions, and exit codes.

Start the standalone WebAssembly console with its `https` profile:

```powershell
dotnet run --project src\NativeDCB.Web --launch-profile https
```

Open `https://localhost:7229`. The browser loads a standalone Blazor WebAssembly application and calls `https://localhost:7154` directly with authenticated gRPC-Web; there is no Web backend-for-frontend. Configure its public OIDC authority/client/scopes in `src/NativeDCB.Web/wwwroot/appsettings.json`. The workbench provides explicit controls for all 34 RPCs, incremental streaming and cancellation, and a prominent NDL editor shared by validation, explanation, and execution. Service navigation, method counts, and wrapper coverage are derived from protobuf descriptors.

The browser endpoint and public OIDC client settings are configured in static content at `src/NativeDCB.Web/wwwroot/appsettings.json`; never put credentials, API keys, or client secrets there. Cross-origin browser calls are allowed only from origins in the server's `GrpcWeb:AllowedOrigins` configuration, which defaults to the two local Web launch origins. CORS remains independent of authentication and authorization.

## Native Commerce Sample

The repository's sole [Native Commerce sample application](samples/Commerce/NativeDCB.Commerce.Sample/README.md) exercises catalog, inventory, carts, checkout, promotions, payments, fulfilment, returns, contention, command reconciliation, and remote payment completion. After the server is running, seed a catalog without publishing events:

```powershell
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- seed http://localhost:5010 commerce-seed
```

Run the complete local commerce lifecycle in a fresh database:

```powershell
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- run http://localhost:5010 commerce-run
```

Configure `RemoteDecisions` before starting the server, then run the trusted remote payment completion scenario in another fresh database:

```powershell
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- remote-payment http://localhost:5010 commerce-remote
```

The manual benchmark suite includes 13 BenchmarkDotNet CPU/filesystem classes and 12 real-server Native Commerce scenarios. List them without running a measurement:

```powershell
dotnet run -c Release --project benchmarks\NativeDCB.MicroBenchmarks -- --list flat
dotnet run -c Release --project benchmarks\NativeDCB.SystemBenchmarks -- --list-scenarios
```

## Documentation

- [Requirements and implementation status](docs/requirements.md)
- [Implemented actor architecture and persistence ownership](docs/actor-architecture.md)
- [OIDC/JWT, generated API-key, and scoped authorization](docs/authentication-authorization.md)
- [Implemented Native Commerce domain and workload model](docs/commerce-domain.md)
- [Implemented benchmark suite, methodology, scenarios, metrics, and reporting](docs/benchmarking.md)
- [Solution structure, projects, tests, and sample](docs/solution-structure.md)
- [Internal engine, actors, NDJSON, state files, and indexes](docs/internal-engine.md)
- [Database lifecycle and health](docs/database-lifecycle.md)
- [Complete gRPC API and errors](docs/grpc-api.md)
- [CLI commands, inputs, output, and examples](docs/cli.md)
- [NativeDCB Decision Language](docs/dsl.md)
- [.NET SDK, plan APIs, analyzer, and generator](docs/dotnet-sdk.md)
