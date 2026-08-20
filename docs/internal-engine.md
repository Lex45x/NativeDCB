# NativeDCB Internal Engine

Status: current implementation reference  
Last verified: 2026-08-19

NativeDCB runs one Orleans localhost silo. `NativeDCB.Actors` owns application orchestration and file mutation; `NativeDCB.Engine` contains Orleans-free event-log, index, and state storage primitives. All public database operations enter actors before reaching Engine storage.

## Correctness Model

The database-keyed `MainWriterGrain` is the only actor path that owns a writable `JsonEventStore`. Orleans non-reentrant turns serialize Main calls. `JsonEventStore` has no in-process semaphore or gate; the OS-exclusive `store.lock` prevents a second process from opening the same database for writing.

A local decision uses **read snapshot, single writer** semantics:

1. The command-keyed `DecisionGrain` captures Handler and Schema snapshots.
2. It builds the command-derived query and asks `IndexOrchestratorGrain.ReadAuthoritativeAsync` for one `{ObservedHead, Events}` result.
3. Index Orchestrator obtains Main's committed head, reads relevant immutable index generations through stateless replicas, and fills the authoritative reader tail from the minimum effective index head through Main's head. Unsupported or missing indexes cause a full bounded partition scan.
4. Decision evaluates outside Main and sends candidate events, the same query, and the observed head to Main.
5. Main checks command reconciliation and later matching events before appending and durably flushing the batch.
6. A conflict causes Decision to discard the model and retry with the Handler and Schema snapshots captured for that invocation.

The observed head bounds the read even if that head event does not match the query. Pending or incomplete records are not visible. Derived indexes can increase or reduce read work but are never append authority.

Remote preparation uses the same actor hydration path and returns a signed, expiring capability containing the query and observed head. Remote completion is routed to the same command-keyed Decision actor and attempts exactly one conditional append. A later matching event returns `stale`; unrelated events after the observed head do not. Completion does not rebuild or retry.

## Orleans Roles

### Main Writer

`IMainWriterGrain`, keyed by database, lazily creates or opens its own `JsonEventStore`, reports lifecycle state, and performs conditional append. It owns `database_v1.json`, `store.lock`, and all event partitions. Candidate schema validation and decision evaluation occur before Main; Main enforces storage-level batch, key, command, and append-condition invariants.

After a committed append, Main derives affected logical index identities directly from event type and keys and dispatches one-way `IIndexGrain.AdvanceAsync` calls. Index actors catch and record derived-file I/O/JSON faults, so those faults do not change the already durable append outcome. Whenever the active partition is greater than one, Main then dispatches `IStateBuilderGrain.BuildMissingAsync`; that state-builder contract is also one-way and may reuse already equivalent files. Opening writer state repeats the state-builder dispatch so a missed post-commit notification is repaired.

### Decision

`IDecisionGrain` is keyed as `{database}|{command UUID}` and owns local execution, remote preparation, and remote completion. It reconciles committed commands through the stateless Read actor, obtains immutable Handler and Schema messages, validates command input, executes the plan, and calls Main. Local append conflicts retry until cancellation or a terminal result.

The stateless `IRemoteDecisionRouterGrain` verifies an HMAC capability before extracting the command identity and routing completion. No preparation continuation is stored in a grain or file.

### Schema And Handler

`ISchemaGrain` and `IHandlerGrain` are each keyed by database and own `schemas_v1.json` and `handlers_v1.json`, respectively. Their ordinary in-memory documents are serialized by Orleans turns. Mutations write a uniquely named temporary file with write-through/durable flush and atomically move it over the owned file.

Schema maintains command/event registrations, versions, fingerprints, compatibility checks, and consistency-key metadata. Handler asks Schema for immutable snapshots when validating NDL or plans, stores compiled plans and fingerprints, and atomically publishes multi-decision statements in one handler-file replacement.

### Read

`IReadGrain`, keyed by database and implemented as a stateless worker, uses `PartitionEventReader` for shared read-only access. It captures committed boundaries directly from metadata and partition files and never acquires `store.lock`, mutates files, or depends on a registry-owned store.

Bounded methods accept an explicit event-ID boundary. Snapshot methods return the partition-recovered boundary together with events. Index advancement/rebuild, finite public reads, command reconciliation, and subscription polling use these contracts. Authoritative Index Orchestrator reads separately obtain Main's promoted head and use Read only through that boundary.

### Index Actors

There is no Index Coordinator. An encoded logical identity is derived directly from database, event type, key name, and key value wherever it is needed.

- `IIndexGrain` is the single mutator for one logical identity. It advances or rebuilds from bounded Read results, creates a write-once generation, and atomically publishes it through `manifest_v1.json`.
- `IIndexReplicaGrain` is a stateless worker that reads the manifest and referenced generation and returns an immutable snapshot.
- `IIndexOrchestratorGrain` is a stateless worker that derives identities, reads replicas in parallel, intersects key indexes within an item, unions event types/items, completes authoritative tails, and handles index listing/rebuild administration.

### State Actors

`IStateGrain`, keyed by database plus partition, owns one `state_{partition}_v1.json`. It inspects through Engine `StateFileInspector` and builds by constructing Engine `StateBuilder` directly. `StateBuilder` opens metadata, prior checkpoints, and authoritative partition files itself; State does not obtain event data through `IReadGrain`.

`IStateBuilderGrain`, keyed by database, receives one-way build-missing notifications after commits when a closed partition exists and sequentially asks each closed-partition State actor to build or reuse its file. `IStateOrchestratorGrain`, also database-keyed, combines Main's active partition, Read partition status, and per-partition State inspection or one-way explicit rebuild dispatch.

Main startup checkpoint recovery remains an Engine operation inside `JsonEventStore.OpenAsync`: it reads state files directly and does not ask State actors for snapshot messages.

### Database Directory And Subscriptions

The singleton `IDatabaseDirectoryGrain` validates names, enumerates database directories, creates through Main, aggregates Main/Schema/Handler status, and reports capabilities and health. There is no `DatabaseRegistry` or mutable process-local database-entry collection.

Each `IEventSubscriptionGrain` has a unique ID and retains only an in-memory cursor, filters, limit, and delivered count. It polls Read snapshots for range follow or authoritative Index Orchestrator snapshots for query subscriptions, in batches of at most 256. It does not consume `JsonEventStore` callbacks, an Orleans stream, or a bounded live queue. gRPC repeatedly calls `ReadNextAsync` and writes returned events.

## Durable Directory

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

The current logical-index directory is `index_{eventHash16}_{keyHash16}`. The generation head is 20 decimal digits and the unique component is a GUID without separators. The pre-refactor `catalog_v1.json` and mutable root `index_*.json` formats are unsupported historical development formats; there is no migration or fallback.

## Partition Identity And NDJSON

Partition filenames are contiguous, one-based, six-digit numbers. Recovery orders by parsed filename and rejects gaps. The highest partition is active; all preceding partitions are closed.

Every partition is NDJSON. The first line is an identity record:

```json
{"kind":"partition","formatVersion":1,"storeId":"...","partitionNumber":1}
```

Recovery requires the header's store UUID, format, and number to match database metadata and the filename-derived number. An event record carries event/batch position, type, schema version, keys, payload, timestamp, command ID, and command type. The final line for a batch is a commit record:

```json
{"kind":"commit","batchId":"...","batchCount":2,"firstEventId":42,"lastEventId":43}
```

Records use camel-case `System.Text.Json`, UTF-8, and one JSON value followed by `\n`. Event IDs in a batch are consecutive. Recovery exposes a batch only after a matching commit record and validates metadata, counts, IDs, keys, command identity, and ordering.

## Append And Rollover

`MaxEventCountPerPartition` defaults to 10,000 and must be positive. Commit/header records do not count. Before append, a non-empty active partition rolls if the full batch would exceed the limit. A batch is never split. A batch larger than the limit occupies one partition and causes a new empty active partition after commit. Reaching the limit exactly also rolls after commit.

One append builds one in-memory byte buffer, writes event records and the commit marker, calls asynchronous flush and `Flush(flushToDisk: true)`, then promotes the in-memory head/events/command map. There is no multi-command flush group, timer, pending-byte threshold, explicit reservation table, or store-local append gate.

If append/flush throws, the store faults because durability may be uncertain. If post-commit rollover fails, the committed result remains valid but future writes are faulted.

## Recovery And Reads

Opening Main acquires `store.lock`, validates metadata and the contiguous partition inventory, then considers `state_*_v1.json` files from newest to oldest. A checkpoint is accepted only when format/store/boundary metadata and fixed fingerprints match, its full event snapshot and command invariants validate, and every covered closed partition still has the recorded length and SHA-256 fingerprint. Recovery seeds from the newest valid checkpoint and replays remaining partitions, or replays from partition 1 when none validates.

Closed partitions must contain only complete records/batches. The active partition may end with a complete uncommitted batch or truncated JSON object. Writer recovery ignores and truncates only that proven suffix. Unknown records, malformed committed JSON, bad headers, inconsistent batches, duplicate command IDs, or invalid ordering are data loss.

Partition-backed Read actors tolerate an incomplete active suffix without trimming it. They open files shared for reading and recover a committed boundary without opening Main. This is not a formal synchronization guarantee for reads racing writer activation/recovery.

## State Files

`state_{partition:000000}_v1.json` is a cumulative derived snapshot through a closed partition. It includes the complete `SequencedEvent` snapshot, known IDs, command summaries, latest IDs per logical key, source partition checkpoints with length/content hashes, an event-snapshot hash, and fixed event/key/state schema fingerprints.

Engine `StateBuilder` holds shared handles on source partitions, may restore the newest valid earlier checkpoint, replays the remaining prefix, hashes each source, writes through an exclusive write-through handle, flushes, and deserializes for boundary validation. An equivalent existing target file is reused; a malformed or non-equivalent target is deleted and rebuilt. Writer startup applies stricter checkpoint validation, including source hashes and event/command coherence.

State files are optional recovery accelerators, not authority. Invalid files are ignored and full authoritative replay remains the fallback.

## Index Files

The logical directory uses the first 16 lowercase hexadecimal SHA-256 characters of the event type and of `keyName + "\0" + keyValue`. Logical identity is also stored and validated in both manifest and generation content.

`manifest_v1.json` identifies the complete published head and generation filename. Publishing creates `generation_{head:D20}_{guid:N}_v1.json` with `FileMode.CreateNew`, durable flushes it, then durably replaces the manifest through a unique temporary file. Generation files are never overwritten or cleaned up by the current implementation.

Indexes are derived snapshots and can be stale after a recorded advancement fault or while a newer generation is being published. `EVENTUAL_INDEX` returns a published immutable view when every query component is supported, otherwise it uses a partition-backed snapshot. `COMMITTED_SCAN` and decision hydration use an authoritative indexed prefix plus a bounded Read tail through Main's observed head. A missing or corrupt generation causes authoritative reads to scan rather than trust it.

## Current Limitations

- Partition-backed reads still scan files; there is no general partition seek/manifest optimization.
- State-file generation and index rebuild can replay large prefixes, though StateBuilder can seed from an earlier valid checkpoint.
- Index generations are retained indefinitely.
- Main awaits index advancement after durable commit, so cancellation/failure after durability can still produce an ambiguous client result even though index actors isolate ordinary derived-file faults.
- Subscriptions poll and are not durable across actor/process failure.
- Prepared remote decisions are stateless bearer capabilities with no continuation store, revocation list, caller binding, or automatic completion retry.
- There are no record/batch/file size limits, compaction, retention, backups, or repair commands for authoritative partitions.
- The design is not safe for multiple silos sharing one filesystem except that `store.lock` rejects a second writer process.
