# NativeDCB Internal Engine

Status: current implementation reference  
Last verified: 2026-08-14

## Correctness Model

NativeDCB currently runs one Orleans localhost silo. A database name is the string grain key for its Main Writer, Read, State Builder, and Index Coordinator grains. The Main Writer is the only actor path that appends events. `JsonEventStore` adds an in-process semaphore and an OS-exclusive `store.lock`, so one process/store instance performs append validation, serialization, durable flush, and state promotion at a time.

A decision uses **read snapshot, single writer** semantics:

1. The Transaction grain asks the Main Writer for the committed head.
2. It reads matching committed events through that head from the Read grain.
3. It evaluates outside the writer.
4. It sends candidate events, the same query, and the observed head to the Main Writer.
5. Under serialized write ownership, the store checks for any later matching event before appending.
6. A conflict causes the Transaction grain to rebuild and retry.

The observed head bounds the read even if that head event does not match the query. Pending or incomplete records are not visible. This preserves DCB correctness without making derived indexes authoritative.

## Orleans Roles

### Main Writer grain

`IMainWriterGrain`, keyed by database name, exposes writer state and conditional append. It delegates durable storage to the registry-owned `JsonEventStore`, validates candidate batches against the current catalog, publishes committed batches to the Index Coordinator, and notifies the State Builder when partitions close.

The grain does not run reducers. Orleans default grain serialization and the store semaphore prevent overlapping appends in the current single-silo process.

### Transaction grain

`ITransactionGrain` is implemented in the server and keyed as `{database}|{command UUID}`. It captures the handler source, compiled plan, schema documents, fingerprints, and command JSON in the request. It checks command-ID reconciliation, validates an optional command schema, executes the plan, and retries append conflicts until cancellation or a terminal result.

The grain has no Orleans-persisted state. Its captured request makes handler replacement/removal irrelevant to that invocation.

### Read grain

`IReadGrain`, keyed by database, serves partition status, range, query, and command-ID reads. It uses `PartitionEventReader`, which opens metadata/partition files for shared reading and reconstructs committed events. Finite public reads use this partition path without opening an unopened store; an open store supplies the Main Writer's promoted head as their boundary. Transaction hydration first asks the Index Coordinator for a snapshot and its minimum index head, then uses this grain to complete the committed tail through the captured writer head. Results are deduplicated and ordered by event ID, so indexes remain non-authoritative.

### State Builder grain

`IStateBuilderGrain`, keyed by database, receives one-way build/rebuild requests. It builds every missing closed-partition state file up to the active partition. Each build restores the newest earlier checkpoint that passes the same metadata, snapshot, and source-partition validation used at writer startup, then replays only the missing closed partitions. If no earlier checkpoint is valid, it rebuilds from the authoritative partition prefix.

### Index Coordinator and Index grains

The coordinator discovers valid index JSON files and tracks logical index identities. A committed event creates identities for every `(event type, key name, key value)` on the event. Each corresponding Index grain receives committed notifications, stores full matching immutable events, advances its observed global head, and persists its file.

The coordinator can answer keyed eventual queries by intersecting key buckets per event type and unioning query items. A query item without both event type and key is not index-supported and falls back to a committed partition scan at the gRPC layer.

## Durable Directory

```text
{database}/
  database_v1.json
  store.lock
  catalog_v1.json
  store_partition_000001_v1.json
  store_partition_000002_v1.json
  state_000001_v1.json
  index_{eventHash16}_{keyHash16}_v1.json
```

`database_v1.json` is normal compact JSON with `formatVersion`, a store UUID, and `createdUtc`. `catalog_v1.json` is indented JSON with current event schemas, command schemas, and handler descriptions/plans. The catalog is server-owned rather than part of the append log.

## Partition Identity And NDJSON

Partition filenames are contiguous, one-based, six-digit numbers. Recovery orders by parsed filename and rejects gaps. The highest partition is active; all preceding partitions are treated as closed.

Every partition is NDJSON. The first line is an identity record:

```json
{"kind":"partition","formatVersion":1,"storeId":"...","partitionNumber":1}
```

Recovery requires the header's store UUID, format, and number to match database metadata and the filename-derived number. This prevents a partition copied from another database from being accepted.

An event line contains:

```json
{
  "kind":"event",
  "eventId":42,
  "batchId":"...",
  "batchIndex":0,
  "batchCount":2,
  "type":"StudentSubscribedToCourse",
  "schemaVersion":1,
  "keys":[{"name":"student","value":"s1"},{"name":"course","value":"c1"}],
  "data":{},
  "timestampUtc":"2026-08-14T12:00:00+00:00",
  "commandId":"...",
  "commandType":"SubscribeStudentToCourse"
}
```

The final line for a batch is:

```json
{"kind":"commit","batchId":"...","batchCount":2,"firstEventId":42,"lastEventId":43}
```

Records use camel-case `System.Text.Json`, UTF-8, and one JSON value followed by `\n`. Event IDs in a batch are consecutive. A batch is recovered only after a matching commit record; all metadata, indexes, counts, IDs, keys, command identity, and ordering are validated.

## Append And Partition Rollover

`MaxEventCountPerPartition` defaults to 10,000 and must be positive. Commit/header records do not count. Before append, a non-empty active partition rolls if the full batch would exceed the limit. A batch is never split. A batch larger than the limit occupies one partition and causes a new empty active partition after commit. Reaching the limit exactly also rolls after commit.

One append currently builds one in-memory byte buffer, writes its event records and commit marker, calls asynchronous flush and `Flush(flushToDisk: true)`, then promotes the in-memory head/events/command map and publishes notifications. There is no multi-command flush group, timer, pending-byte threshold, or explicit pending reservation table. Those remain future optimizations.

If append/flush throws, the store marks itself faulted because durability may be uncertain. If post-commit rollover fails, the committed result remains valid but future writes are faulted.

## Recovery

Opening a store acquires `store.lock`, validates metadata and the contiguous partition inventory, then considers `state_*_v1.json` files from newest to oldest. A candidate is accepted only when its format/store/boundary metadata and fixed fingerprints match, its full event snapshot matches its SHA-256 fingerprint and internal event/command invariants, and every covered closed partition still has the recorded length and SHA-256 content fingerprint. Recovery seeds the in-memory events, command-ID map, head, and covered partition statuses from the newest valid checkpoint, then replays only the remaining partitions.

Malformed JSON, mismatched content, validation failures, and candidate I/O failures are ignored because state files are derived data. If no candidate validates, `JsonEventStore` performs the full authoritative replay from partition 1. Later-partition corruption remains data loss; a checkpoint never makes invalid authoritative tail data acceptable.

Closed partitions must contain only complete records/batches. The active partition may have a final complete-but-uncommitted batch or truncated JSON object. Recovery ignores and truncates only that proven suffix to the byte after the last valid commit. Unknown records, malformed committed JSON, bad headers, inconsistent batches, duplicate command IDs, or invalid ordering stop recovery as data loss.

Partition-backed reads tolerate an incomplete active suffix without trimming it and can open files shared for read while a writer owns the database. Finite EventService snapshot/query/type-and-key reads and CommandService command-ID reads recover a committed head this way when the store is unopened/discovered/recovering, without acquiring `store.lock`; when already open, they are bounded by the Main Writer's promoted head. Follow/subscription and mutations still require opening the store. This shared-reader path is not a formal guarantee of truly concurrent in-process read and writer recovery; see [Database Lifecycle](database-lifecycle.md).

## State Files

`state_{partition:000000}_v1.json` is a cumulative derived snapshot through a closed partition. It includes:

- format, store UUID, source partition, head, and committed event count
- the complete cumulative immutable `SequencedEvent` snapshot
- all known event IDs
- command IDs with first/last IDs and counts
- latest event ID per `(event type, key name, key value)`
- partition checkpoints with boundaries, event counts, lengths, and lowercase SHA-256 content fingerprints
- a lowercase SHA-256 fingerprint of the serialized event snapshot
- fixed event-schema and key-encoding fingerprints plus state-schema fingerprint `native-dcb-main-writer-state-v2`

The builder holds shared read handles on all source partitions, replays the authoritative prefix, hashes each source, and writes the cumulative snapshot through an exclusive write-through handle. It flushes and deserializes the file for boundary validation before release. Existing JSON files with equivalent boundary metadata/schema identity are reused; malformed or non-equivalent files are deleted and rebuilt. This reuse check is less strict than startup validation. `StateFileInspector` reports presence, lock/read failure, JSON validity, metadata/schema identity and event-snapshot fingerprint validity, and an error string; `JsonEventStore.OpenAsync` additionally checks source partition fingerprints and event/command coherence before using a checkpoint.

State files accelerate writer activation only when one validates. They are not authoritative, do not make partition-backed reads seek from a checkpoint, and are built by replaying from partition 1 through each target rather than incrementally extending an earlier state file.

## Index Files

Index filenames use the first 16 lowercase hex characters of SHA-256:

- event component: hash of the event type
- key component: hash of `keyName + "\0" + keyValue`

The JSON content stores the full logical event type/key identity, format version, global index head, full matching event envelopes, and schema/key fingerprints. Logical identity is therefore in the file, not recoverable from the filename hash alone. Writes use a `.tmp` file, durable flush, then overwrite move.

Indexes are asynchronous, derived, and potentially stale. `EVENTUAL_INDEX` may return only the persisted/in-memory index view. `COMMITTED_SCAN` reads authoritative committed partitions through an observed Main Writer head. Missing or corrupt discovered index files are ignored; explicit rebuild rehydrates from partitions. A matching commit also causes a missing index model to rebuild.

## Current Limitations

- Full-log scanning still occurs for partition-backed reads. Writer startup replays only the tail after a valid checkpoint, but falls back to a full replay when none validates; there is no general partition seek/manifest optimization.
- Index-assisted hydration still performs a committed query scan when an index is absent or when completing a lagging tail.
- State-file generation replays the complete prefix for each target, and existing-file reuse validation is less comprehensive than startup checkpoint validation.
- Notifications are in-process events and direct grain calls, not durable pub/sub.
- Index notification failures are swallowed/recorded and do not alter commit success.
- There are no record/batch/file size limits, compaction, retention, checksums, backups, or repair commands for authoritative partitions.
- The design is not safe for multiple silos sharing one filesystem except insofar as the OS lock rejects a second writer process.
