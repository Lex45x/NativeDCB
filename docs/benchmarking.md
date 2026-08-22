# NativeDCB Benchmarking Approach

Status: implemented manual benchmark suite for issue #8
Last verified: 2026-08-21

## Purpose

This document defines how NativeDCB performance is measured, profiled, reported, and interpreted. It covers deterministic microbenchmarks, filesystem benchmarks, and real-server concurrent workloads based on the [Commerce Domain Specification](commerce-domain.md).

The suite is intended to answer where time and memory are spent, how costs grow with history and partition count, how the single-writer boundary behaves under concurrency, and how complete application workflows behave under realistic contention. It is not intended to produce a single universal requests-per-second number.

## Objectives

1. Measure key CPU and allocation costs independently from actor scheduling, transport, and storage.
2. Measure persistence, recovery, index, state, and partition-read behavior against controlled filesystem fixtures.
3. Measure real gRPC workflows against a separately launched NativeDCB server.
4. Profile closed-loop concurrency, open-loop arrival rates, synchronized bursts, and correlated multi-step workflows.
5. Preserve domain correctness during load and classify NativeDCB semantic outcomes separately from transport failures.
6. Produce reproducible result bundles with enough environment data to explain differences.
7. Avoid unstable performance gates or cross-machine claims until dedicated benchmark infrastructure exists.

## Non-Goals

- Benchmarks are not part of the ordinary unit-test timing contract.
- Shared hosted CI results are not treated as stable regression baselines.
- The initial suite does not add production metrics, tracing, or benchmark-specific public APIs.
- Client concurrency is not presented as parallel writing inside one database; Main serializes each database's durable appends.
- Profiled runs are not compared directly with unprofiled throughput runs.

## Benchmark Layers

| Layer | Tool | Process model | Appropriate measurements |
|---|---|---|---|
| CPU microbenchmarks | BenchmarkDotNet | In-process benchmark host | Time, allocations, scaling by input shape |
| Filesystem benchmarks | BenchmarkDotNet | In-process host with isolated temporary storage | Append, recovery, read, index, and state latency |
| System benchmarks | NBomber plus NativeDCB coordinator | Load generator plus real server child process | Throughput, latency distributions, queueing, semantic outcomes |
| External profiling | `dotnet-counters`, `dotnet-trace`, `dotnet-gcdump` | Tool attached to server PID | Runtime counters, traces, GC investigation |

## Why Two Benchmark Engines

BenchmarkDotNet is the required engine for microbenchmarks. It provides controlled warmup, launch isolation, parameterization, diagnosers, allocation reporting, environment capture, and standard exporters. It is appropriate for query matching, schema validation, mapping, compilation, decision-model construction, and Engine operations.

BenchmarkDotNet is not the primary system load generator. The measured system is a long-lived child server with persistent HTTP/2 channels, streaming RPCs, correlated prepare/complete workflows, open-loop arrival rates, synchronized contention bursts, and separate server-process profiling. Implementing those requirements inside BenchmarkDotNet would require a custom scheduler, latency histogram, coordinated-omission handling, process coordinator, and report format while its built-in diagnosers naturally target the load-generator process.

NBomber drives the real-server workloads. It supplies concurrency and arrival scheduling, step measurements, latency distributions, and standard reports while allowing NativeDCB-aware C# workflows and semantic outcome classification. A small repository-owned coordinator remains responsible for process lifecycle, fixture setup, correctness verification, external metrics, and result manifests.

## Project Layout

```text
benchmarks/
  NativeDCB.MicroBenchmarks/
    Decisions/
    Indexes/
    Mapping/
    Ndl/
    Persistence/
    Queries/
    Schemas/
    State/
  NativeDCB.SystemBenchmarks/
    Hosting/
    Metrics/
    Reporting/
    Scenarios/
    Scheduling/
```

Both projects target the repository's selected .NET version and are executable Release projects. `NativeDCB.MicroBenchmarks` pins BenchmarkDotNet 0.15.8; `NativeDCB.SystemBenchmarks` pins NBomber 6.6.0. Package versions are declared directly in project files, following current repository conventions.

NBomber 6.6.0 is free for personal use, but its upstream license requires a commercial subscription for use by or on behalf of an organization. The runner prints this notice. Users must confirm that their intended use complies with the pinned package's license.

The solution gains a `/benchmarks/` folder. Benchmark executables build with the solution but are not discovered by `dotnet test` and do not run during ordinary test execution.

## Shared Commerce Workload

Both benchmark layers reference `NativeDCB.Commerce`. The shared library supplies:

- Command, event, and model contracts.
- NDL and fluent SDK decision definitions.
- Schema and handler registration.
- Deterministic IDs and random seeds.
- Tiny, small, medium, and custom fixture profiles.
- Uniform, Zipfian, and single-hot-key distributions.
- Prebuilt query shapes and expected semantic outcomes.
- Correctness routines that can verify resulting event histories.

The shared library does not contain benchmark timers, NBomber steps, BenchmarkDotNet attributes, process launching, or report code.

## Microbenchmark Methodology

### General Rules

1. Build and run microbenchmarks in Release mode without an attached debugger.
2. Construct immutable input fixtures in global setup unless construction is the operation being measured.
3. Exclude fixture copying, temporary-directory creation, seeding, and cleanup from measured operations.
4. Consume return values through BenchmarkDotNet to prevent dead-code elimination.
5. Report allocations for deterministic CPU benchmarks.
6. Keep success, miss, rejection, and exception paths separate.
7. Record input cardinality and byte size, not only labels such as small or large.
8. Avoid reflection inside measured operations. Internal kernels receive intentional benchmark assembly access or are extracted into testable internal helpers.
9. Do not combine unrelated work into one number. Parsing, validation, mapping, persistence, and transport are measured independently.

### CPU And Allocation Benchmarks

| Benchmark class | Operation | Parameters |
|---|---|---|
| `QueryMatchingBenchmarks` | `EventQuery.Matches` and `QueryItem.Matches` | Items, event types, keys, match position, history size |
| `ConsistencyKeyBenchmarks` | SDK tag extraction and key encoding | Key count, scalar type, string length |
| `SchemaBenchmarks` | Schema parse, payload validation, key extraction | Properties, required ratio, key count, payload size, validity |
| `ActorMappingBenchmarks` | Model/message conversion in both directions | Event count, query shape, payload bytes, keys per event |
| `NdlBenchmarks` | Lex, parse, compile, and format | Source size, decision count, valid/invalid source |
| `SdkDecisionCompilationBenchmarks` | Fluent definition construction and plan compilation | Includes, keys, reducers, requirements, emissions |
| `DecisionModelBenchmarks` | Event replay, structural model construction, evaluation | History size, includes, assignments, rejection position |
| `IndexCombinationBenchmarks` | Key intersection, type/item union, deduplication, head trimming | Snapshot count, overlap, query shape, stale heads |

Recommended cardinalities are powers or meaningful thresholds rather than arbitrary sequences. Representative sets include `1`, `4`, `16`, `64`, `256`, `1,000`, and `10,000`, with larger values used only where iteration duration remains practical.

### Internal Benchmark Seams

Issue #8 may make two behavior-preserving internal refactors:

1. Extract decision replay and evaluation from the actor-oriented runtime into a pure internal decision-model kernel.
2. Extract index snapshot combination from Index Orchestrator into a pure internal query-combination kernel.

Existing behavior receives equivalence tests before benchmarks depend on either helper. Engine and Actors may grant `InternalsVisibleTo` only to the exact microbenchmark assembly. Production types are not made public solely for benchmarking.

### Filesystem Benchmarks

Filesystem benchmarks remain BenchmarkDotNet benchmarks but are reported separately from CPU microbenchmarks.

| Benchmark class | Operation | Parameters |
|---|---|---|
| `AppendBenchmarks` | Conditional durable append | Batch count, payload bytes, keys, existing head, conflict position |
| `RecoveryBenchmarks` | Cold writer open and recovery | Events, partitions, valid/invalid/absent checkpoint |
| `PartitionReadBenchmarks` | Head, range, query, command, and snapshot reads | History, partitions, result size, selectivity, command position |
| `IndexPersistenceBenchmarks` | Generation publication and replica loading | Indexed events, payload size, generations, manifest state |
| `StateBuilderBenchmarks` | Cumulative state generation and equivalent-file reuse | Events, closed partitions, keys, prior checkpoint |

Each iteration starts from a known fixture. Mutable operations receive a fresh copied fixture or fresh database directory outside the measured method. Open file handles are disposed before cleanup.

Storage benchmarks accept an explicit root so a user can select a known local volume. Result metadata records the resolved root, operating system, filesystem information available to .NET, and fixture byte size. Antivirus, encryption, network shares, virtual disks, and power policy can dominate these measurements and must be treated as part of the environment.

Durable append includes write-through behavior and `Flush(flushToDisk: true)`. The suite must not introduce a non-durable benchmark mode and label it as NativeDCB append performance.

### Warm And Cold Filesystem Runs

Warm-cache and cold/restart behavior are distinct:

- BenchmarkDotNet filesystem classes primarily measure warm OS-cache behavior after controlled fixture setup.
- Recovery system scenarios measure new-process startup and first-operation latency.
- A run is not called cold-cache unless the operating system cache was controlled by an explicit platform-specific procedure recorded in the result.

## System Benchmark Architecture

The system benchmark executable contains these components:

```text
System benchmark coordinator
  -> server artifact resolver
  -> isolated port allocator
  -> temporary database-root manager
  -> NativeDCB server process controller
  -> commerce fixture seeder
  -> readiness and warmup controller
  -> NBomber scenario runner
  -> process/runtime metrics sampler
  -> domain correctness verifier
  -> result-bundle writer
```

The coordinator reuses the process pattern established by the process-level end-to-end tests but does not depend on xUnit or TestServer.

### Run Lifecycle

Every independent system run executes these phases in order:

1. Resolve already built Release server and benchmark binaries.
2. Create a unique result directory and database root.
3. Allocate Kestrel, Orleans silo, and Orleans gateway ports.
4. Launch `NativeDCB.Server` with explicit environment configuration.
5. Capture standard output, standard error, server PID, and process exit.
6. Wait for liveness, then verify database readiness through gRPC.
7. Create the database and register commerce schemas and handlers.
8. Populate the selected fixture outside the measurement interval.
9. Wait for required indexes and state files when the scenario depends on them.
10. Warm HTTP/2 channels, serializers, actors, writer activation, and the exact RPC path.
11. Start external process sampling and optional profiling.
12. Run the NBomber measurement phase.
13. Stop measurement and profiling.
14. Verify semantic outcomes, event head, persisted history, and domain invariants.
15. Write reports and captured output.
16. Stop the complete server process tree and delete temporary storage unless retention was requested.

Server startup, schema registration, handler publication, fixture population, index readiness, warmup, verification, shutdown, and cleanup are excluded from steady-state throughput and latency.

### Reset Boundary

NativeDCB has no database delete or unload RPC. A fresh server process and fresh database root are therefore the reliable reset boundary for an independent result. Creating many database names inside one indefinitely running server is allowed only when the scenario explicitly measures warmed multi-database behavior.

## Scheduling Models

| Model | Definition | Primary use |
|---|---|---|
| Closed loop | A fixed number of workers start the next operation after the previous operation completes | Sustainable throughput at bounded concurrency |
| Open loop | Operations are offered at a configured rate regardless of completion | Queueing, overload, and coordinated-omission-resistant latency |
| Synchronized burst | Workers wait on a barrier and start together | Hot-key conflicts, duplicate commands, writer bursts |
| Correlated workflow | One virtual user performs an ordered multi-step domain sequence | End-to-end commerce behavior |

Open-loop reports include offered, scheduled, started, completed, and dropped operation counts. Scheduler delay is reported separately from service latency. Completed-request latency alone is insufficient when the offered rate exceeds capacity.

## Standard Profiles

| Profile | Warmup | Measurement | Default load | Purpose |
|---|---:|---:|---|---|
| Smoke | 2 seconds | 5 seconds | One worker or low rate | Scenario correctness and local setup validation |
| Standard | 15 seconds | 60 seconds | Configured sweep | Comparable local investigation |
| Soak | 5 minutes | 30 minutes | Fixed sustainable load | Growth, leaks, generation accumulation |

Suggested closed-loop concurrency is `1`, `4`, `16`, `64`, and `256`. Suggested synchronized bursts are `16`, `64`, and `256`. Open-loop rates are machine-dependent and supplied explicitly rather than hard-coded as performance expectations.

Database count is a first-class parameter, typically `1`, `4`, or `16`. One database exposes the serialized Main-writer limit. Multiple databases expose process-level actor and storage parallelism.

Scenarios with one-use logical state, including contention and independent prepared slots, execute bounded operation cohorts so an input is never reused. `--operations` sets the exact cohort size. Open-loop defaults it to `ceil(rate * duration)`; other defaults are profile- and scenario-specific. The result summary records whether termination was duration- or iteration-based.

## System Scenario Catalog

### Independent Writes

Each operation uses unique carts, lines, SKUs, and command IDs. It measures durable single-database writer capacity without intentional logical conflicts. Separate runs vary database count.

### Hot Inventory

Many carts reserve one SKU/warehouse with limited stock. The scenario reports committed and domain-rejected outcomes and verifies non-negative inventory. Exact internal retry count is not available because the initial suite adds no production instrumentation.

### Reservation Versus Checkout

Reservation and checkout operations for the same cart start in synchronized bursts. Verification accepts either valid ordering while enforcing that no line is added after checkout and checkout includes every preceding committed line.

### Duplicate Command

Workers submit one command ID concurrently. Exactly one event batch is committed; every other successful response is command reconciliation. The scenario verifies the recovered batch by command ID.

### Complete Purchase

One virtual user executes customer registration or selection, cart open, one or more line reservations, checkout, payment authorization, shipment, and delivery. Step and total workflow latency are reported separately.

### Remote Payment

Preparation and completion are separate measured steps. Variants cover successful completion, unrelated intervening movement, matching staleness, and concurrent completion of one capability.

### Indexed And Authoritative Queries

The same inventory and order query runs with `EVENTUAL_INDEX` and `COMMITTED_SCAN`. Variants cover fully current indexes, stale indexes requiring a tail, missing/corrupt derived data fallback, and unsupported type-only scans.

### Range Streaming

Range snapshots return small and large result sets. Reports include first-item latency, total stream duration, event throughput, and final event ID.

### Subscriptions

Producers append while subscribers observe selected order lifecycle events. The harness records commit-response time and observation time for each correlated event, reporting delivery latency and maximum consumer lag. Subscribers reconnect from a retained cursor in a separate recovery variant.

### Mixed Workload

A configurable mix combines purchases, hot inventory attempts, indexed reads, authoritative reads, remote payment, and subscriptions. The report preserves per-operation metrics rather than collapsing the mix into one latency distribution.

### Partition Rollover And State Generation

A deliberately small partition limit creates frequent rollover and state-file work. Results are kept separate from the production-default partition configuration.

### Recovery

The coordinator stops and restarts the server against fixtures with different heads, partition counts, and checkpoint states. It measures process start, liveness, writer readiness, first read, and first write separately.

## Correctness During Load

Performance results are invalid if domain correctness cannot be verified. Every scenario defines expected invariants before implementation.

Verification includes, as applicable:

- Starting and ending event heads.
- Attempted, committed, rejected, stale, reconciled, and failed operations.
- Committed command IDs and event-batch boundaries.
- Inventory availability and coupon limits.
- Cart, order, payment, shipment, return, and refund uniqueness.
- Stream ordering and expected final event ID.
- Subscription and persisted-history equivalence for the observed interval.
- Index readiness/lag when an indexed scenario requires current indexes.
- Successful command reconciliation after restart.

Transport failures, deadlines, cancellations, domain rejections, stale completions, and already-committed outcomes are distinct categories. A domain rejection is not a gRPC failure. An unexpected transport success with invalid final state still invalidates the run.

## Metrics

### Client And Workload Metrics

- Offered, scheduled, started, completed, dropped, and failed operation counts.
- Request throughput and committed-event throughput.
- p50, p90, p95, p99, p99.9, and maximum latency.
- Open-loop scheduler delay.
- Active and maximum in-flight operations.
- gRPC status counts and deadlines.
- NativeDCB semantic outcome counts.
- Per-step and total correlated-workflow latency.
- Streaming first-item, inter-item, completion, and total latency.
- Subscription commit-to-observation latency and cursor lag.

### External Server Metrics

The initial suite does not modify production code to expose benchmark counters. The coordinator collects externally observable data:

- Server process CPU time and normalized utilization.
- Working set, private memory, and thread count.
- Optional EventPipe runtime and GC counters.
- Process lifetime and unexpected exits.
- Starting and ending event head.
- Public index status and lag.
- Partition and state-file status.
- Database bytes, partition count, state-file count, index directory count, and immutable generation count.
- Captured server logs.

Without production instrumentation, the suite cannot directly report internal decision retry count, indexed-prefix length, authoritative-tail length, or full-scan count. Reports must not infer those values from latency.

## External Profiling Modes

| Mode | Tool | Output |
|---|---|---|
| `none` | None | Unprofiled throughput and latency |
| `counters` | `dotnet-counters` | Runtime/GC counter stream |
| `trace` | `dotnet-trace` | EventPipe trace for CPU/GC analysis |
| `gcdump` | `dotnet-gcdump` | Managed heap snapshot |

The coordinator attaches to the known server PID. Load-generator profiling, when needed, is performed as a separate operator-controlled run so its overhead is not confused with server profiling. A profiled result is labeled and never compared as an equivalent throughput baseline to an unprofiled result. Recovery and derived-index fault scenarios reject profiling because they replace the server PID during measurement.

## Result Bundle

```text
BenchmarkResults/
  {timestamp}-{scenario}-{run-id}/
    manifest.json
    summary.json
    process-metrics.csv
    storage-summary.json
    server.log
    nbomber/
    optional.nettrace
    optional.gcdump
```

`manifest.json` contains:

- Git commit, branch, and dirty status.
- UTC start and end times.
- Server and load-generator binary hashes.
- .NET runtime, operating system, architecture, CPU model where available, processor count, and memory.
- Storage root and available drive/filesystem information.
- Scenario, profile, scheduling model, concurrency, offered rate, database count, and random seed.
- Fixture profile, fixture cardinalities, starting head, and fixture byte size.
- Server configuration including partition limit and logging level.
- Profiling mode and external tool versions.

`summary.json` is a repository-owned stable schema containing scenario parameters, outcome counts, latency percentiles, throughput, correctness status, start/end heads, and paths to detailed artifacts. NBomber's native HTML/CSV/JSON-compatible reports remain available but are not the only machine-readable contract.

Generated benchmark artifacts are ignored by Git unless a result is deliberately curated for documentation.

## Reproducibility And Comparison

1. Compare results only when scenario parameters, runtime, OS, architecture, storage class, durability settings, and profiling mode are equivalent.
2. Record power mode, virtualization, containerization, antivirus, and network-mounted storage manually when relevant.
3. Run enough independent launches to distinguish process-level variance from within-run latency.
4. Keep Windows and Linux baselines separate.
5. Use medians and distributions across runs; do not select only the fastest result.
6. Preserve raw reports when publishing a comparison.
7. Treat improvements below normal environmental noise as inconclusive.
8. Never compare a smoke profile with a standard or soak profile.

The initial implementation provides no automatic regression threshold and no CI workflow. Benchmarks are invoked manually on a controlled developer or dedicated benchmark machine.

## Commands

Microbenchmark examples:

```powershell
dotnet run -c Release --project benchmarks\NativeDCB.MicroBenchmarks -- --filter "*Query*"
$env:NATIVEDCB_BENCHMARK_ROOT = "D:\NativeDCB-Benchmarks"
dotnet run -c Release --project benchmarks\NativeDCB.MicroBenchmarks -- --filter "*Persistence*"
```

System benchmark examples:

```powershell
dotnet run -c Release --project benchmarks\NativeDCB.SystemBenchmarks -- --scenario independent-writes --profile standard --concurrency 1,4,16,64
dotnet run -c Release --project benchmarks\NativeDCB.SystemBenchmarks -- --scenario hot-inventory --profile standard --mode burst --concurrency 64
dotnet run -c Release --project benchmarks\NativeDCB.SystemBenchmarks -- --scenario mixed --profile soak --databases 4 --seed 104729
dotnet run -c Release --project benchmarks\NativeDCB.SystemBenchmarks -- --scenario recovery --profile standard --fixture medium
dotnet run -c Release --project benchmarks\NativeDCB.SystemBenchmarks -- --scenario independent-writes --profile standard --profiling trace
```

Use `--help` for all coordinator options and `--list-scenarios` for the 12 scenario slugs. `--operations` sets an exact bounded cohort, `--rate` is required for open-loop scheduling, and each concurrency sweep point creates an independent server/root/result bundle. Recovery and query-fault scenarios do not accept profiling because their server PID changes.

## Implementation

The implemented suite includes the shared Commerce fixture/query/outcome/verifier library, behavior-covered pure decision and index kernels, all eight CPU/allocation classes, all five filesystem classes, the real-server coordinator, all four scheduling models, all 12 system scenarios, process metrics, optional server profiling, and versioned result bundles.

The suite remains manual. It does not add a CI workflow, automatic threshold, or checked-in cross-machine baseline.

## Acceptance Criteria

- BenchmarkDotNet discovers and executes every benchmark class in Release mode.
- CPU benchmarks report time and allocations with deterministic inputs.
- Filesystem benchmarks isolate mutable fixtures and exclude setup/cleanup from measurement.
- System benchmarks launch a real server process with a fresh root for independent runs.
- Closed-loop, open-loop, synchronized-burst, and correlated-workflow scheduling are available.
- Every system scenario has a smoke profile and post-run correctness verifier.
- Commerce fixtures and semantic rules are shared rather than copied between projects.
- Result bundles contain latency distributions, throughput, semantic outcomes, environment metadata, server logs, and storage growth.
- Optional profiling attaches to the server process and clearly labels profiled results.
- Existing tests remain green and benchmark executables do not run under `dotnet test`.
- Documentation distinguishes deterministic CPU results, environment-sensitive filesystem results, and whole-system results.
- No CI workflow, performance gate, or cross-machine baseline claim is introduced by issue #8.
