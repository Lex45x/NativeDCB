# NativeDCB Database Lifecycle

Status: current actor-owned lifecycle and health behavior
Last verified: 2026-08-19

## Configuration And Identity

`ActorStorageOptions`, consumed through `ActorStoragePath`, reads two top-level configuration keys:

- `DatabaseRoot`: defaults to `%LOCALAPPDATA%\NativeDCB\databases`
- `MaxEventCountPerPartition`: defaults to 10,000 and must be positive

Environment variables can set the same keys. Database Directory creates the absolute root when that singleton actor activates.

### Remote decisions

`PrepareDecision` and `CompleteDecision` are optional. The actor-layer `RemoteDecisionTokenProtector` reads:

```json
{
  "RemoteDecisions": {
    "ActiveKeyId": "2026-08",
    "SigningKeys": {
      "2026-08": "<base64 encoding of at least 32 random bytes>"
    },
    "Lifetime": "00:05:00"
  }
}
```

- `ActiveKeyId` must be non-empty and name an entry in `SigningKeys`.
- Every decoded HMAC-SHA-256 key must contain at least 32 bytes. Keep keys in secret configuration rather than source or browser assets.
- `Lifetime` must be positive and defaults to five minutes when omitted.

The server can start with this section absent or invalid. Only the two remote-decision RPCs then return `FAILED_PRECONDITION`; local execution and all other methods remain available. Environment variables use ASP.NET Core nesting, such as `RemoteDecisions__ActiveKeyId` and `RemoteDecisions__SigningKeys__2026-08`.

New capabilities use `ActiveKeyId`; verification accepts the key ID carried by the capability while it remains configured. Rotation requires adding the new key, retaining old keys through their capabilities' expiry, selecting the new active ID, and restarting. Unexpired capabilities survive restart when the signing key, database store UUID, and relevant Handler/Schema versions remain unchanged because no continuation is persisted.

`model_signature` is an opaque bearer capability. HMAC authenticates its context but does not encrypt it, authenticate a caller, bind a session, or prove how proposed events were chosen. Do not decode, log, publish, or place it in static browser configuration.

A database name is a non-empty relative path below the root. Both slash styles normalize to `/`; rooted names and empty, `.`, or `..` path segments are rejected. The name is the public identifier and the string key for database-scoped actors. There is no rename, delete, unload, restart, or relocation API.

The durable store identity is a UUID in `database_v1.json` and every partition header. No supported operation moves a directory or changes that identity.

## Discovery

There is no `DatabaseRegistry` or mutable `DatabaseEntry`. Whenever Database Directory lists databases or health, `ActorStoragePath.EnumerateDatabases` recursively finds directories containing `database_v1.json` or `store_partition_*_v1.json`. It then asks each database's Main actor for status.

Filesystem discovery does not validate metadata, inventory partitions, acquire `store.lock`, or recover events. A newly activated Main actor starts as `Discovered`, reports loaded head/active partition as `0`, reports read availability from partition 1 presence, and reports write availability as false.

Main activation is lazy. `GetHead`, authoritative query/decision hydration, append, and state administration call `MainWriterGrain.GetStateAsync` and open the writer. Finite range/query/type-and-key reads, command-ID reads, eventual-query fallbacks, index listing/rebuild boundaries, and range-follow polling can recover committed boundaries directly through stateless Read actors without opening Main. Schema and Handler actors own independent files and can activate without opening Main.

## Directory Layout

```text
{DatabaseRoot}/{database}/
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
      generation_{head:20-digits}_{unique}_v1.json
```

The current physical logical-index ID is `index_{eventHash16}_{keyHash16}`. Main exclusively mutates database metadata and event-log files. Schema and Handler exclusively mutate their named files. One Index actor mutates each manifest and creates its generations. One State actor mutates each state file. Stateless Read and Index Replica actors never write.

The largest numbered partition is active; lower contiguous partitions are closed. State files are valid only for closed partitions. The historical pre-refactor `catalog_v1.json` and mutable root index files are unsupported and are not migrated; development databases in that format must be recreated.

## Main States

The protocol and model define:

```text
Discovered -> AcquiringLock -> Recovering -> Ready -> Draining -> Stopped
                                  |             |
                                  +-----------> Faulted
```

These are fields of an activated Main actor, not registry-entry states.

### Discovered

The Main actor has not opened a store. Status and read-file presence are available. Stateless Read actors can validate/recover committed partition data without activating writer ownership.

### AcquiringLock And Recovering

`CreateAsync` or the first `GetStateAsync`/append sets `AcquiringLock`, then `Recovering`, and calls Engine `JsonEventStore.CreateAsync` or `OpenAsync`. Open acquires `store.lock`, validates metadata and contiguous partitions, tries cumulative state checkpoints newest to oldest, and replays the remaining authoritative tail or the full log.

Malformed or invalid derived checkpoints are ignored. A proven incomplete active suffix is truncated. Metadata or committed-partition corruption fails activation. I/O/access failures mark Main `Faulted`; an `InvalidDataException` is not caught by Main's fault transition and can leave the actor reporting `Recovering` while calls fail with data loss.

Stateless Read actors do not await this lifecycle and can perform independent shared partition-file recovery. The implementation does not define stronger synchronization for reads racing in-process writer recovery.

### Ready

Main holds `store.lock` and the active append stream, and its in-memory head, event list, command map, and partition statuses are promoted. Schema and Handler readiness is represented by successful actor activation rather than Main state.

### Draining And Stopped

`MainWriterGrain.OnDeactivateAsync` sets `Draining`, disposes its `JsonEventStore`, releases active and lock handles, then sets `Stopped`. There is no process-wide registry drain phase or public start/stop RPC. A later activation constructs a new Main actor beginning at `Discovered`.

### Faulted

I/O/access failures while opening/writing, uncertain append durability, or post-commit rollover failures can mark Main `Faulted`. New writes are unavailable. Stateless partition reads can still recover committed data independently. There is no automatic restart/repair transition.

## Creation

`CreateDatabase` enters Database Directory, which:

1. normalizes and resolves the relative name below `DatabaseRoot`
2. rejects an existing directory
3. calls the database Main actor to create the directory and acquire `store.lock`
4. durably writes `database_v1.json`
5. durably writes partition 1 with its identity header and opens it for append
6. aggregates the new database snapshot, activating Schema and Handler so empty `schemas_v1.json` and `handlers_v1.json` are durably created
7. returns the Ready database at head `0`

Creation registers no schemas or handlers. Failure disposes partially opened store handles, but there is no transactional directory cleanup/repair workflow.

## Activation And Recovery

The first Main actor call is serialized by its Orleans turn; there is no per-entry semaphore. Opening performs:

1. exclusive `store.lock` acquisition
2. metadata format/store-ID validation
3. contiguous one-based partition discovery
4. newest-to-oldest checkpoint selection with metadata, snapshot, event/command, source length, and SHA-256 validation
5. replay after the accepted checkpoint, or full authoritative replay when none validates
6. partition identity, event, batch, key, command-ID, and commit validation
7. active-suffix truncation only for a complete uncommitted batch or truncated final object
8. active append stream opening and transition to Ready

Schema and Handler files load independently when their actors activate. State and index files are derived and do not determine event authority or Main readiness, except that a valid state file can accelerate Main recovery.

A second process holding the same `store.lock` causes activation to fail with `UNAVAILABLE` and marks Main Faulted. Stateless finite reads remain possible because they do not acquire writer ownership.

## Schema And Handler Lifecycle

Schema and Handler actors independently load or create `schemas_v1.json` and `handlers_v1.json`. Each file contains current registrations plus a monotonic document revision. Mutations copy the current in-memory document, write an indented uniquely named `.tmp` file with write-through/durable flush, atomically move it over the owned file, then promote the replacement in memory.

Schemas and handlers can be registered, replaced, and removed. No superseded versions, statement history, audit records, or caller identity are retained. Handler compilation obtains an immutable Schema snapshot. Multi-decision statement execution validates all decisions and replaces the handler file once; invalid input publishes none.

Schema replacement compatibility rejects removal of required/key properties, type changes, consistency-key-name changes, and newly required properties unless `allow_incompatible` is explicit.

Local decisions retain captured Handler and Schema messages across append-conflict retries. A remote capability records relevant Handler/Schema versions and fingerprints; completion is invalidated when one changes. Once Decision admits a batch, later catalog mutation does not cancel its Main call.

## Command Lifecycle

`ExecuteHandler`:

1. canonicalizes or generates a command ID and enters `DecisionGrain` keyed by `{database}|{commandId}`
2. reconciles an existing command through `ReadGrain.ReadCommandSnapshotAsync`
3. obtains the current Handler and Schema snapshots and validates command input
4. derives the full query and asks Index Orchestrator for an authoritative indexed-prefix plus Read tail snapshot
5. replays includes, evaluates the plan, validates/derives emitted event keys, and conditionally appends through Main
6. retries conflicts without a count limit until cancellation/deadline or a terminal result

One command ID can commit one batch. Rejections are not persisted.

### Remote command lifecycle

`PrepareDecision` enters the same command-keyed Decision actor, performs reconciliation and validation, builds the query, hydrates the model through Index Orchestrator, and returns model JSON plus an expiring HMAC capability. It does not run requirements or emissions and persists no continuation.

`CompleteDecision` first enters the stateless Remote Decision Router, which verifies the capability and routes to the signed command-keyed Decision actor. Decision reconciles before expiry/catalog checks, verifies Handler/Schema versions, validates proposed event order and payloads, derives keys, and calls Main once. Conflict maps to `stale`; completion never rehydrates or retries.

## Read And Subscription Lifecycle

Finite range reads enter stateless `ReadGrain`. Query and type/key reads enter stateless `IndexOrchestratorGrain`. `EVENTUAL_INDEX` reads a published manifest/generation when the complete query is supported and falls back to a partition-backed snapshot when it is not. `COMMITTED_SCAN` opens Main to obtain its promoted head, then returns an authoritative index prefix plus Read tail or a full bounded Read scan.

Range follow and query subscriptions enter a unique `EventSubscriptionGrain`; they do not attach an event callback or consume a live queue. The actor keeps a cursor and polls in batches of at most 256. A range subscription polls partition-backed Read snapshots. A query subscription polls authoritative Index Orchestrator reads, which obtain Main's head. After up to ten empty attempts separated by 100 ms, `ReadNextAsync` returns an empty incomplete result and the gRPC bridge calls it again.

Subscription state is in memory and not durable. Reconnect with the last event ID after interruption. There is no queue-overflow `RESOURCE_EXHAUSTED` path in the current polling implementation.

## Index Lifecycle

- Main derives logical identities from each committed event and dispatches one-way updates to all affected Index actors after the event batch is durable.
- Each Index actor reads its published manifest/generation, asks Read for a bounded missing tail, and publishes a complete new generation through an atomically replaced manifest.
- A missing index rebuilds matching history through its supplied boundary.
- Duplicate or delayed target heads cannot regress or overstate the published complete head.
- Stateless Index Replica actors read current manifests and generations for query use.
- Index Orchestrator derives identities directly; there is no coordinator-owned identity set or mutable root index file.
- Explicit index rebuild captures a partition-backed committed head and awaits all requested Index actor rebuild calls. Index actors record derived-file faults in status rather than throwing ordinary I/O/JSON failures to the RPC.

Indexes can lag without changing writer readiness. Authoritative hydration uses only the index prefix through the minimum relevant published head and completes the remainder from Read through Main's captured head.

## State-File Lifecycle

- After each committed append with an active partition greater than one, Main sends a one-way build-missing notification to the database-keyed State Builder actor after its awaited index advancement.
- State Builder sequentially calls each closed-partition State actor; Engine `StateBuilder` reuses an equivalent target or rebuilds it.
- Each State actor invokes Engine `StateBuilder` directly. The Engine builder opens metadata, earlier state checkpoints, and partition files itself; it does not read event data through `IReadGrain`.
- State files contain cumulative events, command/latest-event summaries, fixed schema fingerprints, an event-snapshot hash, and source partition length/content hashes.
- State Orchestrator asks Main for the active partition, asks Read for partition statuses, and asks each State actor for inspection.
- Explicit rebuild accepts only a closed partition and uses the one-way `IStateGrain.RequestRebuildAsync`; acceptance is dispatch, not completion.

State generation failure does not change event authority or writer readiness. Main startup validates checkpoints directly through Engine recovery and falls back to full authoritative replay.

## Health

`GetHealth` and HTTP readiness enter Database Directory. It enumerates database directories and asks each Main actor for status:

- `read_ready` means partition 1 exists
- `write_ready` means Main is Ready with a non-faulted open store
- state and last fault are returned separately

This is filesystem-presence and actor-state health, not an active integrity probe. `/health/live` returns 200 while the app serves. `/health/ready` returns 200 when every enumerated Main is Ready or Discovered, otherwise 503. An empty root is ready.

## Shutdown

There is no registry disposal, local store gate, or Transaction-grain enumeration. Orleans deactivation invokes Main's deactivation hook, which changes its status to Draining, disposes `JsonEventStore`, completes its currently unused committed-event channel, closes the active stream, releases `store.lock`, and marks the actor Stopped.

There is no configurable drain deadline, subscription completion guarantee, or background-job join. Each append is independently durably flushed before success; non-reentrant Main turns prevent another Main call from overlapping disposal.

## Future Lifecycle Work

- explicit synchronization guarantees for snapshot reads overlapping writer activation/recovery
- explicit repair/restart operations and consistent data-loss state transitions
- transactional database creation across independently owned files
- graceful drain deadlines and subscription/background-work coordination
- authentication/authorization, audit, backup/restore, and safe multi-silo topology
