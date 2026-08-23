# NativeDCB Requirements

Status: requirements reconciled with the current implementation  
Last verified: 2026-08-22

## Purpose

NativeDCB is a Dynamic Consistency Boundaries event store and decision processor for .NET. It stores one globally ordered event sequence per database, uses event type plus named consistency-key values for DCB queries, evaluates decisions in Orleans actors, and conditionally appends an ordered event batch.

This document does not remove unmet requirements. Each area is marked **Implemented**, **Partial**, or **Future**.

## Requirement Status

| Area | Status | Implementation note |
|---|---|---|
| Global append-only sequence and positive `long` event IDs | Implemented | IDs are consecutive in normal operation; recovery validates global ordering. |
| Atomic multi-event command batch | Implemented | Event records followed by a commit record provide recovery atomicity; success follows `Flush(true)`. |
| DCB query and append condition | Implemented | Query items are ORed; event types within an item are ORed; all item keys must match. |
| One writer per database | Implemented | Non-reentrant Main Writer grain plus exclusive `store.lock`; `JsonEventStore` has no semaphore/gate. |
| Consistent decision snapshot and boundary retry | Implemented | Decision obtains an authoritative `{ObservedHead, Events}` result from Index Orchestrator and retries append conflicts until request cancellation/fault. |
| Command-ID reconciliation | Implemented | A command ID maps to at most one committed batch and duplicate execution returns `AlreadyCommitted`. |
| Optional remote decision prepare/complete | Implemented | Preparation returns a hydrated structural model plus an HMAC capability; completion performs one catalog-checked conditional append and reports stale rather than retrying. |
| JSON/NDJSON partitions and crash recovery | Implemented | See [Internal Engine](internal-engine.md). |
| Durable current schema/handler catalog | Implemented | Schema and Handler actors independently replace `schemas_v1.json` and `handlers_v1.json` using unique temporary files. |
| Optional schema validation | Partial | A deliberately small JSON Schema profile is implemented; no general JSON Schema engine or schema history exists. |
| NDL authoring and execution | Partial | Decisions compile and execute. Explain/Execute validate every decision and reject duplicate names; Execute atomically registers all decisions only when the document is valid. Statements still only register handlers, and semantic/type analysis is limited. |
| Fluent .NET authoring | Partial | Schema, builder, plan, and client APIs exist; expression coverage and analyzer/generator coverage are limited. |
| Derived full-event indexes | Implemented | Public eventual reads can return the index snapshot; local and remote decision hydration combines indexed matches with an authoritative committed tail through the captured writer head. |
| Cumulative state files | Implemented as optional recovery checkpoints | A valid full-event checkpoint restores writer state through a closed partition and only the remaining partitions replay; invalid checkpoints are ignored and full authoritative replay remains the fallback. |
| Read availability without writer ownership | Partial | Finite range reads, command-ID reconciliation, eventual-query fallbacks, index administration boundaries, and range-follow polling recover a committed head from shared partitions without opening Main. Authoritative query reads/subscriptions, mutations, state administration, and `GetHead` open Main; concurrent in-process activation/recovery has no separately specified synchronization guarantee. |
| Buffered/grouped flush and pending reservations | Not implemented | Each append writes and flushes one batch under serialized writer access. |
| Multi-silo deployment | Not implemented | Server uses `UseLocalhostClustering`; filesystem coordination is single-process. |
| Native gRPC and browser gRPC-Web transports | Implemented | All eight services expose both transports; browser origins come from `GrpcWeb:AllowedOrigins`. |
| Actor-only database access and orchestration | Implemented | All 31 database-operation RPCs route work through `NativeDCB.Actors`; three authentication RPCs manage server security state and one audit RPC enters the singleton Audit actor. See [Actor Architecture](actor-architecture.md). |
| Dedicated persistent-file ownership | Implemented | Main owns the event log, Schema owns `schemas_v1.json`, Handler owns `handlers_v1.json`, Audit owns the global journal, each Index actor owns one manifest/generation set, and each State actor owns one state file. |
| Replicable index reads | Implemented | One Index actor per logical identity publishes write-once generations consumed by stateless Index Replica and Index Orchestrator actors. |
| Caller authentication and authorization | Implemented | External OIDC/JWTs, generated API keys, exact/wildcard gRPC permissions, handler resource scopes, non-escalating key delegation, bootstrap/recovery, and anonymous liveness/readiness probes. |
| Durable audit and OpenTelemetry observability | Implemented | Mutations, decision outcomes, and denials use a hash-chained journal; protected paginated reads and conditional OTLP logs/traces/metrics are available. |
| Backup/restore | Not implemented | No coordinated backup or restore facility exists for event, catalog, security, derived, and audit files. |

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

The non-reentrant Main Writer actor serializes append condition checks and persistence. `JsonEventStore` does not add a semaphore or gate. The OS-exclusive `store.lock` excludes a second process; there is no parallel-writer protocol.

## Decision Semantics

A `DecisionPlan` contains a name, command schema/alias, keyed includes, ordered evaluation steps, ordered emissions, and language/source/schema fingerprints.

Current execution is:

1. Parse and optionally schema-validate command JSON.
2. Evaluate each include's command-derived key bindings to build the complete `EventQuery`.
3. Ask Index Orchestrator for an authoritative snapshot. It obtains Main's head, combines the authoritative indexed prefix with a Read-grain partition tail, and returns the observed head with ordered events.
4. Use a full committed partition scan when the query is not index-supported or no valid published generation exists.
5. Replay events in ascending event ID. For each event, matching includes run in source order and patch a structural dictionary model.
6. Run `let` and `require` evaluation steps in order. The first failed requirement returns `DomainRejected` and appends nothing.
7. Build one or more candidate payloads. Registered schemas validate payloads and derive keys; without a schema, all non-null scalar payload properties become keys.
8. Submit the batch with the original query and observed head.
9. On conflict, discard the model and repeat. Cancellation/deadline and permanent failures terminate the attempt.

There is no special no-history rejection. Decisions can create an entity from an empty history by using `exists(...)` and accepting when it is false, as the integration tests do. An accepted plan must emit at least one event.

### Remote decision semantics

The optional two-step path uses the same registered plan and hydration code but moves final event selection to a client:

1. `PrepareDecision` validates the command and derives the full matching query from plan includes.
2. It captures the Main Writer head, combines supported index matches with an authoritative committed tail (or scans when unsupported), orders/deduplicates events, and replays include reducers into the structural model.
3. It verifies that the model contains no uninitialized values and returns its JSON with an expiring HMAC capability. Evaluation locals, requirements, decision expressions, and emissions do not run during preparation.
4. The capability binds database/store, command identity/hash, handler/plan, observed query/head, the ordered emission-type sequence, and relevant command/included/emitted schema fingerprints. The server persists no preparation record.
5. `CompleteDecision` requires proposed event type/object payload pairs to match the plan's emission order and count, verifies current relevant catalog state, validates current event schemas, and derives keys server-side.
6. It performs one append. A matching event after the observed head returns `stale`; unrelated head movement does not. There is no automatic server, SDK, or CLI retry.
7. Command-ID reconciliation occurs before expiry/catalog/event checks. The first completion to commit wins and later replays return `already_committed`.

The capability is opaque application data and a bearer credential, not authentication or encryption. Possession permits an attempt to complete with schema-valid payloads matching the plan's ordered emission types; the server does not receive the hydrated model back or prove how the client derived its proposal. Remote decisions still require an executable plan with emissions. Model-only plans, including the separate work tracked by issue #15, are not implemented here.

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

Replacement compatibility rejects removal of old required/key properties, type changes, key-name changes, and newly required properties unless `allow_incompatible` is true. Removal is allowed and persisted history is unchanged. Registration attempts and their created, replaced, unchanged, incompatible, or invalid outcomes are audited without schema documents.

The actor implementation retains this runtime replacement check. It uses independently actor-owned `schemas_v1.json` and `handlers_v1.json` and does not read or migrate the pre-refactor `catalog_v1.json` format.

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
- derived cumulative state files and index manifests with write-once generation JSON files
- state schema `native-dcb-main-writer-state-v2`, full cumulative `SequencedEvent` snapshots, and SHA-256 fingerprints for each event snapshot and covered source partition

State files and indexes are not authoritative. A state file accelerates writer recovery only after metadata, snapshot content, event/command coherence, and covered partition lengths and SHA-256 hashes validate; otherwise recovery ignores it and replays the authoritative log. State construction and partition-backed reads still scan full prefixes. See [Internal Engine](internal-engine.md) for exact records and filenames.

## Public Surface

The implemented public protocol has Database, Catalog, Command, Event, Statement, Administration, Authentication, and Audit services with 35 RPCs in total. CommandService has four methods; AuthenticationService manages generated API keys, and AuditService exposes protected paginated journal reads. All eight services support native HTTP/2 gRPC and gRPC-Web. `NativeDcbClient` and the native CLI wrap every RPC, and the standalone Blazor WebAssembly console exposes the full 35-RPC surface without a BFF. Server integration covers a full SDK flow, remote decisions, browser-style gRPC-Web calls, real JWT/API-key validation, fine-grained authorization, and audit corruption/restart behavior, while process-level coverage launches and restarts a real server against temporary storage. Committed event reads expose only committed events; remote preparation additionally returns hydrated model JSON and an opaque signed capability. Domain rejection and invalid command execution are command response outcomes; routing, lifecycle, authentication, authorization, data-loss, audit, and infrastructure failures use gRPC status codes with protobuf `ErrorDetail` in the `native-dcb-error-bin` binary trailer. See [gRPC API](grpc-api.md) and [CLI](cli.md).

The browser endpoint and OIDC authority/client/scopes in `NativeDCB.Web/wwwroot/appsettings.json` are public configuration. `GrpcWeb:AllowedOrigins` controls which origins browsers permit to read cross-origin responses, but CORS does not authenticate callers or authorize operations. No credentials, API keys, or client secrets belong in Web static assets.

## Scope Boundaries

NativeDCB currently does not provide aggregate streams, payload predicates at the storage boundary, joins/grouping/reporting, mutable history, projections, compaction, retention, upcasting, tenant isolation, or arbitrary server-side code execution.

## Future Requirements

The original design still identifies useful future work, but it must not be assumed by callers:

- build cumulative state files incrementally rather than replaying each complete prefix
- add bounded grouped flush and explicit pending-boundary reservations
- define and test explicit synchronization guarantees for snapshot reads that overlap in-process writer activation/recovery
- define production durability guarantees per filesystem
- add size/backpressure limits for records, batches, requests, and full-event indexes
- add audit retention/archival, coordinated backup/restore, repair tooling, and operational restart policy
- define schema evolution/upcasting and compatibility/version policy
- support multi-silo topology only with a safe shared-storage coordination design

## References

- [DCB specification](https://dcb.events/specification/)
- [Internal Engine](internal-engine.md)
- [Database Lifecycle](database-lifecycle.md)
- [NativeDCB gRPC API](grpc-api.md)
- [Authentication and authorization](authentication-authorization.md)
- [Observability and audit](observability-audit.md)
- [NativeDCB Decision Language](dsl.md)
- [NativeDCB .NET SDK](dotnet-sdk.md)
