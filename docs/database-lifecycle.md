# NativeDCB Database Lifecycle

Status: current server lifecycle and health behavior  
Last verified: 2026-08-14

## Configuration And Identity

`DatabaseRegistry` reads two top-level configuration keys:

- `DatabaseRoot`: defaults to `%LOCALAPPDATA%\NativeDCB\databases`
- `MaxEventCountPerPartition`: defaults to 10,000 and must be positive

Environment variables can set the same keys. The root is converted to an absolute path and created when the server starts.

A database name is a non-empty relative path below the root. Both slash styles are normalized to `/`; rooted names and empty, `.`, or `..` path segments are rejected. Names are registry keys, public gRPC database identifiers, and Orleans string grain keys. There is no rename, delete, unload, restart, or relocation API.

The durable store identity is a UUID in `database_v1.json` and every partition header. The relative database name can change if an operator moves the directory between server runs, but no supported operation performs or validates such a move beyond store identity checks.

## Discovery

At process startup the registry recursively finds directories containing `database_v1.json` or `store_partition_*_v1.json`. Each is added as a `Discovered` entry. Discovery does not validate metadata, inventory partitions, load the catalog, acquire `store.lock`, or recover events. Consequently:

- `ListDatabases`, `GetDatabaseInfo`, `GetHealth`, and HTTP readiness do not activate a discovered database.
- an unopened database reports loaded head/active partition as `0`
- read availability is a simple check for `store_partition_000001_v1.json`
- write availability is false until activation succeeds

Activation is lazy. Catalog calls, command execution, administration, statements, `GetHead`, range follow, and event subscriptions use `DatabaseRegistry.GetAsync` and open the database. Finite EventService snapshot/query/type-and-key reads and `GetEventsByCommandId` use the registry entry without opening it; when no store is open, they recover their committed boundary directly from shared partition-file reads.

## Directory Layout

```text
{DatabaseRoot}/{database}/
  database_v1.json
  store.lock
  catalog_v1.json
  store_partition_000001_v1.json
  store_partition_000002_v1.json
  state_000001_v1.json
  index_{eventHash16}_{keyHash16}_v1.json
```

The largest numbered partition is active. A lower partition is closed by the existence of the next contiguous partition. State files are valid only for closed partitions. Catalog, state, and index files are JSON; partitions are NDJSON. Exact identity and record content is in [Internal Engine](internal-engine.md).

## States

The protocol and model define:

```text
Discovered -> AcquiringLock -> Recovering -> Ready -> Draining -> Stopped
                                  |            |
                                  +----------> Faulted
```

### Discovered

Known directory, not open. Metadata/health listing is available. `ReadAvailable` can be true based on the first partition filename. Finite EventService reads and command-ID reads can validate/recover committed partition data without activating the writer; live follow/subscription and mutations cannot.

### AcquiringLock

Set immediately before activation attempts the exclusive `store.lock`. This transition happens synchronously inside the triggering request and is generally brief.

### Recovering

Set before `JsonEventStore.OpenAsync`. Recovery validates metadata and the contiguous partition inventory, then tries cumulative state files from newest to oldest. A valid checkpoint restores the committed event snapshot and command map through its closed source partition; recovery verifies its metadata, schema and event-snapshot fingerprints, event/command coherence, and each source partition's length and SHA-256 content fingerprint before replaying only later partitions. Malformed JSON, validation failures, and candidate I/O failures are ignored. If none is valid, recovery authoritatively replays from partition 1. A proven incomplete active suffix is truncated in either path. The catalog is loaded only after the event store is open.

While the store remains unopened, including an entry observed in `Recovering`, finite snapshot/query/type-and-key and command-ID reads use independent shared partition-file recovery rather than awaiting writer activation. The implementation does not define a stronger synchronization guarantee for truly concurrent in-process read recovery and writer recovery over the same entry.

### Ready

The store and catalog are loaded, the writer lock and active append stream are held, and reads/writes/catalog/administration are available.

### Draining

`ApplicationStopping` calls `BeginDrain`, changing Ready entries to Draining. Batch validation and command/statement/catalog paths reject new writes based on this state. Read methods do not contain a separate draining prohibition, but process shutdown can cancel/terminate them.

### Stopped

Set during registry disposal after store handles and the writer lock are released. It is normally only observable during process teardown; there is no start/stop RPC.

### Faulted

I/O/access failures while opening/writing, uncertain append durability, or post-commit rollover failures can mark the registry entry Faulted. New writes are unavailable. Finite snapshot/query/type-and-key and command-ID reads do not require a new activation and can still recover committed partition data when no store opened successfully; an already-open faulted store is read through the Main Writer's last promoted head. There is no comprehensive per-operation availability policy or automatic recovery transition.

Authoritative `InvalidDataException` corruption maps to gRPC `DATA_LOSS`. Some activation data-loss paths are not caught by `DatabaseEntry.OpenAsync`'s fault transition and can leave status `Recovering` while calls continue to fail; callers should treat the status plus failed operation as diagnostic rather than an automatic repair state.

## Creation

`CreateDatabase`:

1. validates/resolves the relative name below `DatabaseRoot`
2. rejects an existing registry entry or directory
3. creates the directory and acquires `store.lock`
4. durably writes `database_v1.json`
5. durably writes partition 1 with its identity header and opens it for append
6. creates an in-memory Ready entry and durably saves empty `catalog_v1.json`
7. publishes the entry in the registry

A created database is Ready at head `0`. Creation does not register schemas or handlers. Failure disposes an opened store, but there is no transactional directory cleanup/repair workflow for partially created files.

## Activation And Recovery

The first activating request is serialized by a per-entry semaphore. Concurrent callers wait and then reuse the open store.

Opening performs:

1. exclusive `store.lock` acquisition
2. metadata format/store-ID validation
3. partition discovery, numeric ordering, and contiguous one-based numbering validation
4. newest-to-oldest state-checkpoint selection, accepting the first candidate whose metadata, cumulative event snapshot, SHA-256 snapshot fingerprint, and source partition lengths/content fingerprints validate
5. restoration of the accepted checkpoint followed by NDJSON replay of only the remaining partitions, or authoritative replay of all partitions when no checkpoint is valid
6. partition identity, event, batch, key, command-ID, and commit validation during replay
7. active-suffix truncation only for a complete uncommitted batch or truncated final object
8. active append stream opening
9. current catalog load, or creation of an empty catalog if missing
10. transition to Ready

State and index files remain derived and do not determine committed authority or gate Ready. A valid state file can seed writer recovery; an invalid state file is ignored rather than blocking the authoritative partition replay. Index files are not read to restore writer state.

A second process holding the same database's `store.lock` causes activation to fail with `UNAVAILABLE` and transitions the entry to Faulted. The lock-failure integration test first verifies that a discovered database remains Discovered and serves a finite snapshot read while competing ownership prevents writer activation, then verifies that `GetHead` attempts activation and reports the lock failure.

## Catalog Lifecycle

`catalog_v1.json` contains only current event schemas, command schemas, and handlers. Mutation takes a catalog semaphore, updates in-memory dictionaries, writes an indented `.tmp` catalog with write-through/durable flush, and moves it over the catalog. On save failure, the in-memory mutation is rolled back.

Schemas/handlers can be registered, replaced, and removed. No superseded versions, statement history, audit records, or caller identity are persisted. Handler execution receives a complete immutable actor request, so later catalog mutation does not change that command attempt or its conflict retries.

Catalog mutation is coded for Recovering or Ready, but because activation/catalog loading is synchronous, normal calls reach it in Ready. Draining/Faulted/Discovered/Stopped entries reject changes or fail activation.

## Command Lifecycle

`ExecuteHandler`:

1. activates the database and canonicalizes/generates command ID
2. reconciles an already committed command before handler/payload checks
3. requires Ready/write-available and resolves the current handler
4. parses command JSON as an object
5. sends a captured handler, plan, schemas, command, and IDs to Transaction grain `{database}|{commandId}`
6. validates the optional command schema
7. repeatedly captures writer head, reads matching history through it, evaluates, and conditionally appends
8. returns commit, already-committed, rejection, or failed outcome

Conflict retries have no count limit and are bounded by request cancellation/deadline or terminal failure. One command ID can commit one batch. Rejections are not persisted.

## Read And Subscription Lifecycle

Finite range, query, and type-and-key RPCs do not activate an unopened database. They recover a finite committed head from shared partition-file reads and scan through it; an `EVENTUAL_INDEX` request also falls back to this scan while unopened. If the database is already open, snapshot operations obtain the Main Writer's promoted head and remain bounded by it; `COMMITTED_SCAN` is authoritative through that boundary and `EVENTUAL_INDEX` may return a stale derived view.

Range follow and subscription still activate/open the database and require writer ownership. They attach an in-process event callback before replay, replay through a captured head, suppress already-written event IDs, then consume a bounded live queue. There is no durable server subscription state. Shutdown/drain does not implement an explicit catch-up barrier; clients should reconnect with the last event ID after server interruption.

Read grain scans tolerate an incomplete active suffix. Pending writes are invisible until durable flush and in-memory promotion.

## Index Lifecycle

- Main Writer sends every newly committed batch to the Index Coordinator after commit.
- The coordinator creates logical identities from every event key and forwards each batch/head to all known Index grains.
- A new/missing index rebuilds matching history through the supplied head.
- Valid files are loaded lazily; invalid/malformed files are ignored or reported Faulted.
- Index writes use temporary files and replacement.
- Explicit rebuild is one-way and status must be polled.

Indexes can lag without changing writer readiness. Transaction decisions use the minimum relevant index head as a snapshot boundary, query the authoritative committed tail through their captured writer head, then deduplicate and order both sets before reduction.

## State-File Lifecycle

- Partition rollover causes Main Writer to notify the State Builder.
- The builder loops over every closed partition and creates/reuses its cumulative state file.
- The builder reads authoritative partitions, writes with an exclusive handle, durably flushes, and validates the result.
- Each state file contains the complete cumulative `SequencedEvent` snapshot, command and latest-event summaries, the `native-dcb-main-writer-state-v2` schema fingerprint, a SHA-256 event-snapshot fingerprint, and a SHA-256 content fingerprint for every source partition.
- Explicit rebuild accepts only a closed partition and is one-way.
- Inspector status reports missing, locked/building, invalid JSON/schema or event-snapshot fingerprint, or valid. The stricter startup path also verifies source partition fingerprints and state event/command coherence.

State generation failure does not change committed event authority or writer readiness. Startup uses only a fully validated checkpoint and falls back to authoritative full recovery when checkpoints are absent or invalid. State generation applies that same validation to the newest earlier checkpoint and replays only the remaining closed-partition prefix, with full authoritative rebuilding as its fallback.

## Health

### gRPC `GetHealth`

Always reports process `live=true`. For each entry:

- `read_ready` means partition 1 exists
- `write_ready` means status Ready and loaded store not faulted
- state and last fault are returned separately

This is registry/file presence health, not an active probe of unopened database integrity.

### HTTP health

- `/health/live` always returns HTTP 200 while the app serves requests.
- `/health/ready` returns 200 when all entries are Ready or Discovered, otherwise 503.

Discovered databases therefore do not block HTTP readiness even though they have not been validated or opened.

## Shutdown

On host stopping, Ready entries become Draining. During dependency-injection disposal, each store waits for its local gate, completes its committed-event channel, closes the active partition stream, releases `store.lock`, and becomes Stopped.

There is no explicit configurable drain deadline, Transaction-grain enumeration, pending group-flush coordinator, subscription completion guarantee, or background-job join. Each append is independently flushed before success, and store disposal cannot enter its gate until a current append exits it.

## Future Lifecycle Work

- explicit synchronization and availability guarantees for snapshot reads overlapping in-process activation/recovery/fault transitions
- explicit repair/restart operations and consistent data-loss state transitions
- durable catalog/database creation transactions
- graceful drain deadlines and subscription/background-work coordination
- authentication/authorization, audit, backup/restore, and multi-silo topology
