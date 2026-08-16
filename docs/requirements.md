# NativeDCB Requirements

Status: requirements reconciled with the current implementation  
Last verified: 2026-08-14

## Purpose

NativeDCB is a Dynamic Consistency Boundaries event store and decision processor for .NET. It stores one globally ordered event sequence per database, uses event type plus named consistency-key values for DCB queries, evaluates decisions in Orleans actors, and conditionally appends an ordered event batch.

This document does not remove unmet requirements. Each area is marked **Implemented**, **Partial**, or **Future**.

## Requirement Status

| Area | Status | Implementation note |
|---|---|---|
| Global append-only sequence and positive `long` event IDs | Implemented | IDs are consecutive in normal operation; recovery validates global ordering. |
| Atomic multi-event command batch | Implemented | Event records followed by a commit record provide recovery atomicity; success follows `Flush(true)`. |
| DCB query and append condition | Implemented | Query items are ORed; event types within an item are ORed; all item keys must match. |
| One writer per database | Implemented | Main Writer grain plus in-process store gate and exclusive `store.lock`. |
| Consistent decision snapshot and boundary retry | Implemented | Transaction captures writer head, reads through it, and retries append conflicts until request cancellation/fault. |
| Command-ID reconciliation | Implemented | A command ID maps to at most one committed batch and duplicate execution returns `AlreadyCommitted`. |
| JSON/NDJSON partitions and crash recovery | Implemented | See [Internal Engine](internal-engine.md). |
| Durable current schema/handler catalog | Implemented | `catalog_v1.json` is atomically replaced using a temporary file. |
| Optional schema validation | Partial | A deliberately small JSON Schema profile is implemented; no general JSON Schema engine or schema history exists. |
| NDL authoring and execution | Partial | Decisions compile and execute. Explain/Execute validate every decision and reject duplicate names; Execute atomically registers all decisions only when the document is valid. Statements still only register handlers, and semantic/type analysis is limited. |
| Fluent .NET authoring | Partial | Schema, builder, plan, and client APIs exist; expression coverage and analyzer/generator coverage are limited. |
| Derived full-event indexes | Implemented | Public eventual reads can return the index snapshot; transactions combine indexed matches with an authoritative committed tail through the captured writer head. |
| Cumulative state files | Implemented as optional recovery checkpoints | A valid full-event checkpoint restores writer state through a closed partition and only the remaining partitions replay; invalid checkpoints are ignored and full authoritative replay remains the fallback. |
| Read availability without writer ownership | Partial | Finite EventService snapshot/query/type-and-key reads and command-ID reconciliation recover a committed head from shared partition reads when the store is not open. Follow/subscription, mutations, and `GetHead` still require writer activation; concurrent in-process activation/recovery has no separately specified synchronization guarantee. |
| Buffered/grouped flush and pending reservations | Not implemented | Each append writes and flushes one batch under serialized writer access. |
| Multi-silo deployment | Not implemented | Server uses `UseLocalhostClustering`; filesystem coordination is single-process. |
| Native gRPC and browser gRPC-Web transports | Implemented | All six services expose both transports; browser origins come from `GrpcWeb:AllowedOrigins`. |
| Security, audit, observability, backup/restore | Not implemented | No authentication, authorization, audit sink, metrics/tracing, backup, or restore facility is configured. |

## Core Invariants

The implementation enforces these invariants:

1. A database is one logical event sequence, not identifier-partitioned aggregate streams.
2. Every committed event has a positive, globally ordered `long` event ID, used as its sequence position.
3. Events and keys are immutable after commit and reads preserve event-ID order.
4. A command appends at least one event; all events from that command share a command ID/type and commit as one batch.
5. Every event has at least one unique, non-empty `(name, value)` consistency key.
6. An append condition rejects a candidate when a committed event after the observed head matches its complete query.
7. Unrelated events do not conflict unless the supplied query matches them.
8. A command ID identifies at most one committed batch.
9. Only batches with a matching commit marker are visible after recovery.
10. Partition and index files are bound to logical identities stored in their content, not inferred solely from enumeration order or hashed names.

The engine validates append conditions and persistence under one serialized store gate. Orleans also serializes calls to a Main Writer grain. This is the current single-writer correctness mechanism; there is no parallel-writer protocol.

## Decision Semantics

A `DecisionPlan` contains a name, command schema/alias, keyed includes, ordered evaluation steps, ordered emissions, and language/source/schema fingerprints.

Current execution is:

1. Parse and optionally schema-validate command JSON.
2. Evaluate each include's command-derived key bindings to build the complete `EventQuery`.
3. Read the Main Writer head.
4. Ask the Read grain for every matching committed event through that head.
5. Replay events in ascending event ID. For each event, matching includes run in source order and patch a structural dictionary model.
6. Run `let` and `require` evaluation steps in order. The first failed requirement returns `DomainRejected` and appends nothing.
7. Build one or more candidate payloads. Registered schemas validate payloads and derive keys; without a schema, all non-null scalar payload properties become keys.
8. Submit the batch with the original query and observed head.
9. On conflict, discard the model and repeat. Cancellation/deadline and permanent failures terminate the attempt.

There is no special no-history rejection. Decisions can create an entity from an empty history by using `exists(...)` and accepting when it is false, as the integration tests do. An accepted plan must emit at least one event.

## Schema Profile

Schemas are optional current-catalog metadata. Registration uses UTF-8 JSON and a raw-document SHA-256 fingerprint.

The supported profile is:

- root JSON object, with absent `type` or `"type": "object"`
- required object-valued `properties`
- each property has one string `type`: `string`, `integer`, `number`, `boolean`, `object`, `array`, or `null`
- optional `required` array of unique declared property names
- optional `additionalProperties`; only literal `false` disables extras
- event consistency keys declared on direct scalar properties with `x-native-dcb-consistency-key`
- the legacy alias `consistencyKey` is also accepted by the server, but new schemas should use `x-native-dcb-consistency-key`
- event schemas require at least one key and logical key names must be unique

The implementation does not support `$ref`, composition, unions, enum/const, string/number constraints, defaults, formats, nested key paths, or general JSON Schema validation. Registered command schemas validate command object shape. Registered event schemas validate emitted/appended payloads and require supplied keys to equal schema-derived keys.

Replacement compatibility rejects removal of old required/key properties, type changes, key-name changes, and newly required properties unless `allow_incompatible` is true. Removal is allowed and persisted history is unchanged. Overrides are not audited in the current implementation.

## Storage Requirements

Implemented storage behavior includes:

- `database_v1.json` database identity and creation time
- six-digit, contiguous, one-based partition filenames
- a partition identity header carrying format version, store UUID, and partition number
- newline-delimited event and commit objects
- configurable event-count rollover without splitting a batch
- an exclusive database writer lock
- durable flush before acknowledgement
- truncation of a provably incomplete suffix in the active partition
- rejection of malformed committed prefixes, invalid identities, missing partition numbers, and inconsistent batches
- derived cumulative state and full-event index JSON files
- state schema `native-dcb-main-writer-state-v2`, full cumulative `SequencedEvent` snapshots, and SHA-256 fingerprints for each event snapshot and covered source partition

State files and indexes are not authoritative. A state file accelerates writer recovery only after metadata, snapshot content, event/command coherence, and covered partition lengths and SHA-256 hashes validate; otherwise recovery ignores it and replays the authoritative log. State construction and partition-backed reads still scan full prefixes. See [Internal Engine](internal-engine.md) for exact records and filenames.

## Public Surface

The implemented public protocol has Database, Catalog, Command, Event, Statement, and Administration services with 27 RPCs in total. All six services support native HTTP/2 gRPC and gRPC-Web. `NativeDcbClient` wraps every RPC while retaining a legacy constructor that exposes only its former Command/Event/Catalog subset. The native CLI and standalone Blazor WebAssembly console each expose the full 27-RPC surface; Web calls the server directly and has no BFF. Server integration covers a full SDK flow and browser-style gRPC-Web unary and streaming calls, while process-level end-to-end coverage launches a real server against temporary storage. Only committed events cross the API. Domain rejection and invalid command execution are command response outcomes; routing, lifecycle, data-loss, and infrastructure failures use gRPC status codes with protobuf `ErrorDetail` in the `native-dcb-error-bin` binary trailer. See [gRPC API](grpc-api.md) and [CLI](cli.md).

The browser endpoint in `NativeDCB.Web/wwwroot/appsettings.json` is public configuration. `GrpcWeb:AllowedOrigins` controls which origins browsers permit to read cross-origin responses, but CORS does not authenticate callers or authorize operations. No secrets belong in Web static assets, and the unauthenticated server and console must not be exposed to untrusted networks.

## Scope Boundaries

NativeDCB currently does not provide aggregate streams, payload predicates at the storage boundary, joins/grouping/reporting, mutable history, projections, compaction, retention, upcasting, tenant isolation, or arbitrary server-side code execution.

## Future Requirements

The original design still identifies useful future work, but it must not be assumed by callers:

- build cumulative state files incrementally rather than replaying each complete prefix
- hydrate transactions from index snapshots plus a partition tail
- add bounded grouped flush and explicit pending-boundary reservations
- define and test explicit synchronization guarantees for snapshot reads that overlap in-process writer activation/recovery
- define production durability guarantees per filesystem
- add size/backpressure limits for records, batches, requests, and full-event indexes
- add authenticated authorization, audit records, metrics/tracing, backup/restore, repair tooling, and operational restart policy
- define schema evolution/upcasting and compatibility/version policy
- support multi-silo topology only with a safe shared-storage coordination design

## References

- [DCB specification](https://dcb.events/specification/)
- [Internal Engine](internal-engine.md)
- [Database Lifecycle](database-lifecycle.md)
- [NativeDCB gRPC API](grpc-api.md)
- [NativeDCB Decision Language](dsl.md)
- [NativeDCB .NET SDK](dotnet-sdk.md)
