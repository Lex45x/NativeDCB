# NativeDCB gRPC API

Status: implemented `nativedcb.v1` protocol reference  
Last verified: 2026-08-14

## Contract

The wire contract is `src/NativeDCB.Protocol/Protos/v1/nativedcb.proto`. JSON bodies are UTF-8 bytes. UUIDs are canonical `D` strings. Event IDs are non-negative `int64`; `0` is the empty head or an absent partition boundary in status responses. Database names are relative paths below configured `DatabaseRoot`.

Queries contain ORed items. Event types in an item are ORed; all keys in an item must be present. An omitted/empty query means all events. Streams preserve ascending committed event ID. Only committed batches are returned.

## Transports And Clients

Every RPC in all six services is available over both transports:

- Native gRPC over HTTP/2 is the transport for the .NET SDK, CLI, sample, and other non-browser generated clients. The default native endpoint is `http://localhost:5010`.
- gRPC-Web is enabled on all six mapped services for browser clients. The server's `https` launch profile exposes `https://localhost:7154` as well as the native `http://localhost:5010` endpoint.

The server reads browser origins from `GrpcWeb:AllowedOrigins` and exposes the gRPC status/error headers required by the client. Its defaults allow `http://localhost:5094` and `https://localhost:7229`, matching the Web launch profiles. A different static host origin must be added explicitly. CORS is enforced by browsers and is not an authentication or authorization boundary; the current server implements neither and must not be exposed to untrusted networks.

The repository has two operator clients with the complete 29-RPC surface:

- `NativeDCB.Cli` maps one command to every RPC and uses native gRPC. See [CLI](cli.md).
- `NativeDCB.Web` is a standalone Blazor WebAssembly application with explicit controls for every RPC, direct gRPC-Web calls, incremental stream output and cancellation, and a prominent NDL editor. It has no BFF.

The browser's `NativeDCB:ServerAddress` is public static configuration in `src/NativeDCB.Web/wwwroot/appsettings.json` and defaults to `https://localhost:7154`; it must never contain secrets. The .NET `NativeDcbClient` also wraps every RPC for application use over native gRPC.

## DatabaseService

| RPC | Implemented behavior |
|---|---|
| `ListDatabases` | Lists discovered registry entries without opening them. Returns state, currently loaded head, read/write flags, and fault. A discovered unopened database reports head `0`. |
| `CreateDatabase` | Creates a non-existing relative directory, metadata, partition 1, empty catalog, and opens it Ready at head 0. |
| `GetDatabaseInfo` | Returns registry state, version strings `1`, loaded head/active partition, lock/read/write flags, current catalog fingerprints, and last fault. It does not open a Discovered database. |
| `GetHealth` | Returns process `live=true` plus per-database state, `read_ready`, `write_ready`, and fault. It does not activate databases. |
| `GetCapabilities` | Returns protocol/NDL `v1`, file format `1`, query features `range`, `type`, `keys`, `committed-scan`, subscriptions enabled, and a fixed disclosed partition limit of 10,000. The reported limit is currently not read from custom configuration. |
| `GetHead` | Lazily opens/recovers the database and returns the Main Writer committed head. |

The server also exposes HTTP `GET /health/live` (`200`, `{live:true}`) and `GET /health/ready`. Readiness is `200` only when every registry entry is `Ready` or `Discovered`; otherwise it is `503`. An empty registry is ready.

## CatalogService

| RPC | Implemented behavior |
|---|---|
| `RegisterEventSchema` | Parses the supported schema profile, requires an event key, checks replacement compatibility, and durably updates the current catalog. |
| `RegisterCommandSchema` | Equivalent current-catalog registration without the event-key requirement. |
| `RemoveSchema` | Removes an event or command schema and returns its old fingerprint. It does not rewrite events or handlers. |
| `GetSchema` | Returns the current event or command schema document and fingerprint. The kind is required. |
| `ListSchemas` | Lists current schema summaries ordered by kind and name. An unspecified kind includes both event and command schemas. |
| `RegisterHandler` | Registers exactly one handler either from NDL source or serialized `DecisionPlan` in `plan_json`. Invalid source/plan returns a normal response with `handler.valid=false` and diagnostics; it does not replace a valid existing handler. |
| `RemoveHandler` | Removes the current handler and returns source/plan fingerprints. |
| `GetHandler` | Returns current source, command type, fingerprints, and `valid=true`. `include_plan_json` includes the authoritative stored plan. `generate_ndl` requests a canonical NDL representation and returns `NDL3001` generation diagnostics when lossless conversion is impossible. |
| `ListHandlers` | Lists current handlers ordered by name. |
| `ValidateNdl` | Parses and performs the server's limited semantic/schema validation without persistence. Event transient schemas participate; command transient schemas are accepted but not used for semantic validation. |

Catalog mutation requires `Recovering` or `Ready`, though recovery is performed synchronously inside the activating call and is not normally externally concurrent with a catalog request. Catalog reads expose current state, not superseded registrations or history. `allow_incompatible` affects schema replacement compatibility. It is carried for handlers but no handler compatibility override/audit logic is currently implemented.

Schema details are in [Requirements](requirements.md). Diagnostics currently include lexer/parser `NDL0001`-style codes, server `NDL2001`, plan `PLAN1001`/`PLAN2001`, and compatibility `SCHEMA2001`.

## CommandService

### `ExecuteHandler`

The request contains database, handler, optional canonical non-empty command UUID, and command JSON bytes. The server creates a UUID when omitted. It checks for an existing committed command before handler lookup and payload validation, allowing ambiguous-result reconciliation even if the handler was removed.

The response always echoes command ID/type and has one outcome:

- `committed`: first/last event ID plus all committed envelopes
- `already_committed`: original first/last event ID, without inline events
- `rejected`: currently code `DomainRejected`, reason, and `{}` details
- `failed`: an `ErrorDetail`, most commonly `InvalidCommand`

Boundary conflicts are retried inside the Transaction grain. Cancellation/deadline interrupts execution. Writer/storage failures use transport statuses rather than a `failed` outcome.

### `GetEventsByCommandId`

Returns the complete committed batch for a canonical command UUID. Missing IDs, including domain-rejected attempts, return `NOT_FOUND`. For an unopened/discovered/recovering entry it recovers a committed head through shared partition-file reads without opening the store or acquiring writer ownership. If the store is already open, the read is bounded by the Main Writer's promoted head.

## EventService

Finite range, query, and type-and-key reads do not activate an unopened store. For unopened/discovered/recovering entries they recover a committed head from partition files opened for shared reading and read only through that boundary. For an already-open entry they capture the Main Writer's promoted head, so records not yet promoted after durable append are outside the snapshot. This path does not establish a broader guarantee about truly concurrent in-process writer recovery. Follow mode and `SubscribeEvents` still open the store and require writer ownership.

### `ReadEventsByRange`

- `after_event_id` is exclusive and must be non-negative.
- `through_event_id`, when present, is inclusive and must be greater than `after_event_id`; values beyond head are clamped to head.
- `limit`, when present, is `1..Int32.MaxValue`.
- unspecified and `SNAPSHOT` modes capture a finite committed head without activating an unopened store.
- `FOLLOW` replays through a captured head, then tails in-process committed notifications. It forbids `through_event_id` and ends when its optional limit is reached.

### `ReadEventsByQuery`

Uses the same bounds/limit rules and a DCB query. `EVENTUAL_INDEX` uses indexes only when the store is already open and every item has an event type and at least one key; otherwise it scans. It can return a stale index view. Unspecified and `COMMITTED_SCAN` scan committed partitions through the observed head.

### `ReadEventsByTypeAndKeys`

Builds one query item from one required event type and zero or more keys. A type-only request is valid and uses a partition scan because it is not index-supported. Like the other finite reads, this does not activate an unopened store.

### `SubscribeEvents`

Subscribes after an exclusive event ID. It installs a live listener, replays a committed-scan query through a captured head, deduplicates by event ID, then tails notifications. Optional simple keys are an additional AND filter over the optional DCB query. There is no durable subscription state; reconnect with the last event ID.

Follow/subscription live queues hold 1,024 events. A consumer that overflows the queue receives `RESOURCE_EXHAUSTED`. Delivery is process-local and at-least-once behavior around reconnect should be assumed; event ID is the deduplication key.

## StatementService

### `ExplainStatement`

Parses source, applies limited server semantic/schema checks to every decision, rejects duplicate decision names, and returns diagnostics. Valid decisions receive a summary/fingerprint and query templates whose values are the placeholder `$command`. No inferred schemas are currently produced.

### `ExecuteStatement`

Current behavior is narrower than the result union suggests. It accepts an NDL document containing one or more uniquely named decision definitions, validates/compiles each decision independently, formats each as standalone source, and atomically publishes all current handler registrations to `catalog_v1.json` only when every decision is valid. Invalid documents, including duplicate decision names, publish none. The stream emits:

1. a diagnostic batch when diagnostics exist,
2. one `RegistrationResult(kind="handler")` per decision,
3. a final `StatementCompletion`.

It does **not** execute event reads, commands, schema statements, or administrative statements, and it does not emit `StatementResult.event` or `.command`. `allow_incompatible` is not used on this path. There is no statement audit/history persistence.

## AdministrationService

| RPC | Implemented behavior |
|---|---|
| `ListPartitions` | Returns ordered partition filename, active flag, visible first/last IDs, committed count, and inspected state-file status. |
| `ListIndexes` | Returns discovered/known index identity, hashed filename, index/main heads, lag, hydration state, and last fault. |
| `GetStateFileStatus` | Inspects one existing partition's corresponding state file. |
| `RequestIndexRebuild` | Requires event type and keys, sends one-way rebuilds through the coordinator, and reports queued/accepted. |
| `RequestStateRebuild` | Requires a positive closed partition number, sends a one-way State Builder rebuild, and reports queued/accepted. |

Rebuild acceptance is not completion. Poll status/list operations to observe results.

## Error Model

Normal command-domain outcomes stay in `ExecuteHandlerResponse`. The implemented transport mapping is:

| gRPC status | Current uses |
|---|---|
| `INVALID_ARGUMENT` | Invalid database path/name, UUID, range/limit/mode/consistency, query keys/items, schema JSON, schema kind, rebuild identity, or required routing values explicitly checked by a service. |
| `NOT_FOUND` | Database, handler, schema, command ID, or partition not found. |
| `ALREADY_EXISTS` | Database directory/registry entry already exists. |
| `FAILED_PRECONDITION` | State rebuild requested for the active or future partition. |
| `RESOURCE_EXHAUSTED` | Follow/subscription live queue overflow. |
| `UNAVAILABLE` | Database not write-available, writer lock contention, event-store fault, I/O, or access failure. |
| `DATA_LOSS` | Invalid/corrupt authoritative metadata or partition data. |
| `INTERNAL` | Sanitized fallback for an unexpected service exception; the original exception is logged server-side. |
| `CANCELLED` / `DEADLINE_EXCEEDED` | Standard gRPC request lifetime behavior. |

Every transport `RpcException` created by `ProtocolMapper` carries a serialized protobuf `ErrorDetail` in the binary trailer `native-dcb-error-bin`. Its code and message are stable for the mapped error, its message matches the gRPC status detail, and `details_json` is `{}`. The interceptor preserves existing `RpcException` values and cancellation behavior. It logs unexpected service exceptions and maps them to `INTERNAL` with code `Internal` and the sanitized message `An unexpected server error occurred.` rather than exposing exception details.

`ErrorDetail` is also used inside command failures, health/database summaries, index/state status, and other response messages. The custom trailer is not a `google.rpc.Status` envelope and no retry metadata is supplied.

## Not Implemented

Authentication/authorization, audit records, external statement history, pagination, protocol negotiation, configurable message limits, durable subscriptions, server retry hints, and standard `google.rpc.Status` details are future work.
