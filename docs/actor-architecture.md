# NativeDCB Actor Architecture

Status: current implementation reference
Last verified: 2026-08-19

## Goals

The server is an actor host and transport adapter. Database lifecycle, catalog operations, decisions, reads, subscriptions, indexes, state files, and persistence coordination execute inside Orleans actors.

The implemented architecture enforces these boundaries:

1. gRPC maps protobuf messages to actor messages, selects the responsible actor, propagates cancellation, and maps the result back to protobuf.
2. gRPC does not open database files, access a registry/store object, hold database locks, select index fallbacks, execute decisions, or coordinate calls between database components.
3. Every persistent file type is mutated through its owning actor type.
4. Replicated read actors may consume committed event-log files or immutable derived-file generations, but never mutate them.
5. Cross-component workflows are orchestrated by domain actors, not generic service facade actors.
6. Actor messages, rather than protobuf messages or concrete storage types, cross Orleans boundaries.

The implemented actor boundary does not add multi-silo event-log writing. The Main actor and the existing OS `store.lock` remain the single-writer boundary. Read and index-replica roles are stateless, but a future multi-silo deployment would still require appropriate shared storage and coordination.

## Project Boundary

`NativeDCB.Actors` contains Orleans contracts, immutable actor messages, actor implementations, decision execution, schema/handler ownership, and actor-to-actor orchestration. Its namespaces are rooted at `NativeDCB.Actors`, including `Contracts`, `Messages`, `Grains`, `Catalog`, `Decisions`, `Mapping`, and `Storage`.

```text
NativeDCB.Model   <- storage-neutral domain contracts
NativeDCB.Ndl     <- parsing, formatting, and plan compilation
NativeDCB.Engine  <- Orleans-free event-log, index, and state storage primitives
NativeDCB.Actors  <- Orleans actors and application orchestration
NativeDCB.Server  <- ASP.NET Core host, protobuf mapping, and stream bridging
```

The gRPC implementations use `NativeDCB.Actors.Contracts`, `NativeDCB.Actors.Messages`, and mapping code; they do not open database storage. `NativeDCB.Engine` has no Orleans dependency and supplies event-log, index, and state storage primitives to `NativeDCB.Actors`. Actor contracts use Orleans-generated `[GenerateSerializer]`, `[Immutable]` messages. Protobuf messages, `JsonDocument`, mutable catalog objects, and concrete storage classes do not cross actor boundaries.

## Actor Ownership

| Actor | Key | Ownership and responsibility |
|---|---|---|
| `DatabaseDirectoryGrain` | singleton | Database-name validation, filesystem discovery, creation orchestration, database information/health aggregation, and server capabilities |
| `MainWriterGrain` | database | Sole actor owner of `database_v1.json`, `store.lock`, and `store_partition_*_v1.json`; lazy writer lifecycle, head, command reconciliation, and conditional append |
| `ReadGrain` | database, stateless worker | Shared read-only partition access and committed-boundary capture without acquiring `store.lock` |
| `SchemaGrain` | database | Sole mutator of `schemas_v1.json`; command/event registrations, fingerprints, versions, compatibility, payload validation data, and key metadata |
| `HandlerGrain` | database | Sole mutator of `handlers_v1.json`; NDL/plan validation, compilation, fingerprints, versions, and statement publication |
| `DecisionGrain` | database plus command ID | Local execution, remote preparation/completion, authoritative model hydration, evaluation, and local conflict retry |
| `RemoteDecisionRouterGrain` | stateless worker | Capability verification and routing of completion to the command-keyed Decision actor |
| `IndexGrain` | encoded logical index identity | Sole mutator of one index manifest and its write-once generation files |
| `IndexReplicaGrain` | encoded logical index identity, stateless worker | Read-only loading of the published manifest and referenced immutable generation |
| `IndexOrchestratorGrain` | database, stateless worker | Direct identity derivation, parallel replica reads, query set operations, authoritative tail completion, listing, and rebuild orchestration |
| `StateGrain` | database plus partition | Sole mutator of one `state_*_v1.json`; direct Engine `StateBuilder` execution and inspection |
| `StateBuilderGrain` | database | One-way build-missing fan-out across closed partitions to per-partition State actors |
| `StateOrchestratorGrain` | database | Partition/state listing, closed-partition validation, inspection, and explicit rebuild dispatch |
| `EventSubscriptionGrain` | unique subscription ID | Cursor, filtering, deduplication, limits, and polling of authoritative actor reads |

Actor names describe roles rather than public services. There is no DatabaseService, CatalogService, or CommandService facade actor. A gRPC method calls the actor that owns the requested operation. An orchestrator exists only where a domain operation spans multiple owners.

## Transport Boundary

All 31 database-operation RPCs cross the actor boundary. The three `AuthenticationService` RPCs manage the server-owned API-key catalog and do not enter database actors.

| Public RPC | Actor entry point |
|---|---|
| `ListDatabases` | Database Directory |
| `CreateDatabase` | Database Directory, which calls the new database's Main, Schema, and Handler actors |
| `GetDatabaseInfo` | Database Directory, which aggregates immutable Main, Schema, and Handler status |
| `GetHealth` | Database Directory |
| `GetCapabilities` | Database Directory |
| `GetHead` | Main |
| `RegisterEventSchema`, `RegisterCommandSchema`, `RemoveSchema`, `GetSchema`, `ListSchemas` | Schema |
| `RegisterHandler`, `RemoveHandler`, `GetHandler`, `ListHandlers`, `ValidateNdl` | Handler, which obtains immutable Schema snapshots when required |
| `ExecuteHandler`, `PrepareDecision` | Decision |
| `CompleteDecision` | Remote Decision Router, then Decision |
| `GetEventsByCommandId` | Read |
| `ReadEventsByRange` | Read; follow mode uses a unique Event Subscription actor |
| `ReadEventsByQuery`, `ReadEventsByTypeAndKeys` | Index Orchestrator |
| `SubscribeEvents` | Event Subscription |
| `ExecuteStatement`, `ExplainStatement` | Handler |
| `ListPartitions`, `GetStateFileStatus`, `RequestStateRebuild` | State Orchestrator |
| `ListIndexes`, `RequestIndexRebuild` | Index Orchestrator |

Generating an omitted command UUID, validating transport-shaped arguments, and constructing an Orleans actor key remain transport concerns. Every one of the 31 database-operation RPC implementations then enters the responsible actor. Streaming gRPC methods loop over `EventSubscriptionGrain.ReadNextAsync`; cursor, filtering, limits, polling, and deduplication remain in that actor.

## Decision Flow

### Local execution

1. gRPC canonicalizes or generates the command ID and calls `Decision(database, commandId)`.
2. Decision reconciles the command through the stateless Read actor before validating handler or payload input.
3. Decision obtains an immutable Handler revision and the relevant immutable Schema registrations.
4. Decision validates the command and builds the complete command-derived event query.
5. Decision asks Index Orchestrator for the latest authoritative result for the query.
6. Index Orchestrator obtains Main's committed head, reads the latest relevant index replicas in parallel, treats the minimum usable index head as the authoritative prefix boundary, and fills the remaining tail through Read.
7. Index Orchestrator returns `{ ObservedHead, Events }` as one immutable result.
8. Decision replays the ordered events, evaluates requirements/locals, constructs the ordered candidate batch, and validates payloads against its captured Schema revisions.
9. Decision calls Main with the candidates, complete query, returned observed head, and captured schema versions.
10. Main reconciles the command ID and atomically checks the append condition and persists the batch. A conflict causes Decision to discard the model and repeat with the same captured Handler and Schema revisions. Cancellation or a terminal outcome ends the attempt.

Main does not execute reducers or read schema/handler files. Candidate events admitted by Decision already contain server-derived consistency keys and their captured schema version. Main validates storage-level batch invariants and remains the only append authority. `JsonEventStore` has no semaphore or append gate; non-reentrant Main turns serialize the actor path, while `store.lock` excludes another process.

### Remote preparation

Remote preparation uses the same Decision actor and hydration path through step 7. Decision serializes the structural model without running evaluation, requirements, or emissions, then creates the expiring HMAC capability. No preparation record is persisted.

### Remote completion

1. gRPC forwards database, signature, and proposed events to Remote Decision Router.
2. The router verifies the capability, obtains the signed command ID, and calls the corresponding Decision actor.
3. Decision reconciles an already committed command before expiry or catalog checks.
4. Decision validates expiry, store identity, Handler revision, relevant Schema revisions, and the signed emission order/count.
5. Decision validates proposed payloads and derives keys from its admitted Schema snapshot.
6. Decision calls Main once with the signed query and observed head.
7. Main returns committed, already committed, or conflict; Decision maps conflict to stale and does not retry.

## Captured Catalog Revisions

Schema and Handler actors expose immutable snapshots with monotonic revisions. Catalog changes do not require a cross-actor lock or distributed transaction.

- A local decision keeps the Handler and Schema revisions captured when the attempt begins, including across append-conflict retries.
- A remote capability records the relevant Handler and Schema identities/revisions. A change before completion validation invalidates the capability.
- Once Decision has admitted an append under captured revisions, a later catalog mutation does not invalidate that in-flight append.
- Persisted events carry the captured event-schema version.
- Schema replacement compatibility remains a runtime rule. Replacing a schema rejects removed required/key properties, type changes, key-name changes, and newly required properties unless `allow_incompatible` is explicitly set.

Schema/handler history and audit records are not introduced. Revisions provide actor concurrency identity, not a public catalog-history API.

## Durable Directory

The current layout is:

```text
{database}/
  database_v1.json
  store.lock
  schemas_v1.json
  handlers_v1.json
  store_partition_000001_v1.json
  store_partition_000002_v1.json
  state_000001_v1.json
  indexes/
    {logical-index-id}/
      manifest_v1.json
      generation_00000000000000000042_{unique}_v1.json
```

The physical logical-index directory is currently `index_{eventHash16}_{keyHash16}`. Each generation filename contains a 20-digit head and a unique GUID component. Generation files are created once; `manifest_v1.json` is durably replaced to publish a generation. There is no `catalog_v1.json` or mutable root-level `index_*.json` compatibility path; pre-refactor development databases must be recreated.

Authoritative event-log formats remain unchanged. Schema and Handler actors durably replace their own current-state JSON files using unique temporary files, write-through flush, and atomic move. State and index files remain derived and may be discarded and rebuilt.

## Index Replication

Index mutation and index reads use separate roles.

1. Main derives each affected identity directly from committed event types and keys and dispatches one-way `IndexGrain.AdvanceAsync` calls after the append is durable.
2. Each Index actor compares the target with its published head and asks the stateless Read actor for the missing authoritative range.
3. It builds a complete snapshot through the target, creates a write-once generation file, then atomically replaces its manifest.
4. Duplicate, delayed, and out-of-order notifications are safe because an actor never advertises a head beyond the complete snapshot it persisted.
5. Index Replica actors read the current manifest and then its referenced immutable generation. Atomic manifest replacement means a replica observes either the previous or next complete generation. Generation cleanup is out of scope, so a manifest never references a concurrently deleted file.
6. Index Orchestrator derives encoded actor identities directly from event types and keys; there is no `IndexCoordinator` or mutable database-wide identity registry.
7. It intersects keys within a query item, unions event types/items, and returns one atomic `{supported, head, events, fault}` result per logical index.
8. `ReadEventualAsync` returns the stale immutable index view when the complete query is index-supported; an unsupported or absent index uses `ReadGrain.ReadQuerySnapshotAsync`, which captures a committed boundary from partitions without opening Main.
9. `ReadAuthoritativeAsync` obtains Main's committed head, reads the latest index snapshots, bounds the indexed prefix, and fills from the minimum effective index head through the captured head using Read. Unsupported queries use a full committed scan.
10. It returns `{ ObservedHead, Events }` atomically. Decision hydration and public `COMMITTED_SCAN` queries use this operation. A stale or missing index can increase work but cannot omit committed matching events.

Public `EVENTUAL_INDEX` reads may return the stale immutable index view. Authoritative decision hydration never treats an index as the source of truth.

## Reads And Subscriptions

Read actors are stateless workers. Main remains the only event-log mutator, while readers open committed partitions with shared read access and never trim or repair files.

Read contracts distinguish boundary capture from bounded reads:

- `CaptureHeadAsync` reconstructs the committed head from partition files.
- `ReadRangeAsync` and `ReadByQueryAsync` read through an explicit caller-supplied boundary.
- `ReadRangeSnapshotAsync`, `ReadQuerySnapshotAsync`, and `ReadCommandSnapshotAsync` capture a partition-backed boundary and return it with the data.

Index actors use explicit bounded Read calls for advancement and rebuild. Index Orchestrator uses Main's head plus bounded Read calls for authoritative reads and partition-backed snapshots for eventual fallbacks. Main's index notifications are one-way and do not change the already durable commit result; each Index actor records derived-storage I/O/JSON faults for administration status.

Event Subscription does not use an Orleans stream or an in-process event callback. It retains a cursor and polls Read or authoritative Index Orchestrator snapshots in batches, waiting briefly when no new events are available. gRPC only bridges `ReadNextAsync` results to the response stream. Subscription state is not durable across actor or process failure.

## State Files

One `StateGrain` owns each cumulative state file. The actor constructs Engine `StateBuilder` directly for its database directory. That builder opens authoritative partition files itself, may seed from the newest valid earlier state checkpoint, replays the remaining closed-partition prefix, writes the target state file, flushes it, and validates it. It does **not** obtain event data through `IReadGrain`.

After a committed append, whenever the active partition is greater than one, Main invokes the database-keyed `StateBuilderGrain.BuildMissingAsync`; that contract is one-way, and the builder sequentially asks each per-partition State actor to build or reuse every closed-partition file. `StateOrchestratorGrain` combines Main's active-partition boundary, Read partition status, and State inspection/rebuild calls. Explicit state rebuild is dispatched through the one-way `IStateGrain.RequestRebuildAsync`. Main recovery still reads and validates state checkpoints directly through Engine recovery code; it does not request checkpoint data from State actors.

## Concurrency And Locks

There is no `DatabaseRegistry`, mutable `DatabaseEntry`, registry semaphore, catalog semaphore, `JsonEventStore` semaphore/gate, or Index Coordinator. Default non-reentrant Orleans turns serialize Main, Schema, Handler, Index, State Builder, State, and orchestrator actor state. Unique temporary files plus durable replacement protect schema, handler, and manifest publication; generations are write-once. The OS-exclusive `store.lock` remains because Orleans activation serialization alone cannot exclude a second process.

## Current Boundaries

- All 31 database-operation gRPC methods route database work into actors; the three authentication methods remain server security operations.
- Local and remote model construction occurs in command-keyed Decision actors through Index Orchestrator.
- Finite reads and subscription polling contain no direct storage access in gRPC.
- Schema, Handler, Main, Index, and State actors are the mutating paths for their respective files.
- Stateless Read and Index Replica actors only consume authoritative partitions or published immutable generations.
- The deployment remains one localhost silo; shared-filesystem multi-silo safety is not implemented.
