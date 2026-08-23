# NativeDCB Observability And Audit

Status: implemented reference for [issue #1](https://github.com/Lex45x/NativeDCB/issues/1)
Last verified: 2026-08-22

## Overview

NativeDCB has a durable server-wide audit journal, protected paginated audit reads, audit-aware readiness, BCL activities/meters in Engine and Actors, and server-side OpenTelemetry registration for logs, traces, and metrics. OTLP export is conditional on standard OpenTelemetry configuration, and can be disabled completely with `OTEL_SDK_DISABLED=true`.

This document defines the implemented behavior and explicit boundaries for issue #1.

## Implemented Properties

The implementation:

- durably audit every public state-changing operation
- durably audit every local and remote decision outcome, including non-mutating outcomes
- durably audit authentication and authorization denials for every gRPC action
- preserve caller identity without storing credentials or bearer capabilities
- record an attempt before an authorized mutation or decision reaches its implementation
- record the authoritative outcome at the actor or server component that determines it
- fail closed before mutation when the audit attempt cannot be persisted
- expose protected, paginated audit reads over gRPC, the SDK, CLI, and Web console
- export structured logs, traces, and metrics through OpenTelemetry OTLP
- use bounded metric dimensions and explicit sensitive-data exclusions
- preserve the existing actor ownership, native gRPC, and gRPC-Web boundaries

## Non-Goals

The implementation does not:

- durably audit successful routine reads, successful audit queries, or normal stream item delivery
- place audit records in the public domain event sequence
- retain complete request, response, event, command, schema, plan, or NDL bodies
- provide audit retention, deletion, compaction, archival, or legal-hold policy
- encrypt audit files or make them tamper-proof against a host administrator
- make audit and independently owned event/catalog/security files transactionally atomic
- add a Prometheus scrape endpoint
- add repair, restart, backup, restore, or multi-silo coordination
- replace application-specific gRPC health with OpenTelemetry

## Audit Coverage

### Mutations

The following operations receive a durable `attempt` before execution and an authoritative `outcome` afterward:

| Area | Operations | Outcome examples |
|---|---|---|
| Database | `CreateDatabase` | created, already exists, invalid, failed |
| Schema catalog | register/remove event or command schema | created, replaced, unchanged, removed, not found, incompatible, invalid, failed |
| Handler catalog | register/remove handler | created, replaced, unchanged, removed, not found, invalid, failed |
| Statements | `ExecuteStatement` | published, rejected, failed |
| Decisions | `ExecuteHandler`, `PrepareDecision`, `CompleteDecision` | committed, already committed, rejected, prepared, stale, expired, invalidated, failed |
| Administration | index and state rebuild requests | dispatched, completed, faulted, failed |
| API keys | create and revoke | created, revoked, already revoked, rejected, failed |
| Bootstrap | automatic creation and offline recovery | created, replaced, failed |

`PrepareDecision` is included even though it does not mutate server state. It creates a bearer capability and is part of the complete decision audit.

Internal local-decision append conflicts are represented by telemetry retry counts, not by additional final audit outcomes. One public decision attempt receives one final decision outcome.

### Denials

An unauthenticated or unauthorized gRPC call receives one durable `denied` record. This includes denied reads, streams, health/capabilities RPCs, audit queries, and mutations. Anonymous HTTP `GET /health/live` and `GET /health/ready` probes are not audited.

A denial remains `UNAUTHENTICATED` or `PERMISSION_DENIED` if the audit sink is also unavailable. The sink failure is logged, counted, and reflected in readiness; it does not replace the security result.

### Routine Reads

Successful database, catalog, event, administration-status, API-key-list, and audit-query reads are observable through request traces and metrics but do not enter the durable audit journal. This avoids turning high-volume event reads and long-lived subscriptions into a global audit-write bottleneck.

## Record Model

Every record has a globally ordered positive sequence and belongs to one operation UUID. The record schema is versioned independently from the public protocol.

| Field | Meaning |
|---|---|
| `schema` | Record schema, initially `native-dcb-audit-record-v1`. |
| `sequence` | Positive global audit sequence. |
| `timestamp_utc` | Server timestamp when this record was appended. |
| `operation_id` | UUID shared by the attempt and outcome. |
| `phase` | `attempt`, `outcome`, `denied`, or `system`. |
| `category` | `database`, `catalog`, `decision`, `administration`, `security`, `authorization`, or `system`. |
| `operation` | Stable operation name, normally the fully qualified gRPC method. |
| `authentication_scheme` | `ApiKey`, `JwtBearer`, `Disabled`, `System`, or absent for unauthenticated callers. |
| `subject` | Stable API-key ID or JWT subject. |
| `issuer` | JWT issuer when applicable. |
| `database` | Normalized database when one is safely known. |
| `resource` | Canonical encoded handler, API-key, audit, or administration resource when applicable. |
| `outcome` | Stable bounded outcome name. |
| `code` | Stable domain or transport code when applicable. |
| `grpc_status` | Final transport status for denied or transport-failed operations. |
| `trace_id` | W3C trace ID when an activity exists. |
| `command_id` | Canonical command UUID for decision correlation when applicable. |
| `first_event_id` / `last_event_id` | Committed range when applicable. |
| `revision` | Catalog or derived-state revision/head when applicable. |
| `previous_hash` | SHA-256 hash of the preceding record. |
| `record_hash` | SHA-256 hash of the canonical record content and `previous_hash`. |

Optional fields are omitted rather than serialized as invented values. Labels are descriptive metadata and are not stable identities. API-key audit identity uses the public key ID, never its label alone.

### JWT Identity

JWT audit identity is the tuple `JwtBearer`, validated issuer, and `sub`. A missing or blank `sub` makes an otherwise authenticated token unsuitable for an audited mutation or decision and causes that operation to fail before mutation. `Identity.Name`, email, display name, and arbitrary claims are not canonical audit identities.

### Disabled Authentication

Explicit disabled authentication uses the stable subject `authentication-disabled`. Such records remain visibly distinguishable from JWT and API-key callers and are expected only from tests and benchmark baselines.

## Sensitive Data

Audit records, trace attributes, metric tags, and structured application logs must not contain:

- `Authorization` headers
- JWT access tokens or token claim dumps
- raw API-key credentials or secret hashes
- bootstrap or recovery credentials
- remote-decision `model_signature` bytes
- hydrated model JSON
- command or event JSON
- schema documents, plan JSON, or NDL source
- consistency-key values
- OTLP authentication headers

Safe routing metadata includes normalized database names, handler names, schema names, API-key IDs, command IDs, partition numbers, event ranges, catalog fingerprints, revisions, and bounded outcome/error codes. Trace and audit access must still be restricted because this metadata can be operationally sensitive.

## Journal Ownership And Layout

A process-lifetime `AuditJournal` singleton acquires the lock during hosted-service startup and releases it only during host disposal. The singleton Orleans `AuditGrain` is the sole normal-runtime append/query path over that journal. Files live below `DatabaseRoot`:

```text
{DatabaseRoot}/
  .audit/
    audit.lock
    audit_v1.json
    audit_partition_000001_v1.ndjson
    audit_partition_000002_v1.ndjson
```

`audit_v1.json` contains the audit store UUID, schema version, creation time, and configured rollover limit. Each partition begins with a versioned identity header bound to the audit store UUID and partition number.

Records use canonical UTF-8 JSON followed by one newline. A record is acknowledged only after asynchronous flush and `Flush(flushToDisk: true)` complete. Audit files are authoritative operational records, not derived state.

The default rollover limit is 10,000 records per partition and is configurable through `Audit:MaxRecordCountPerPartition`. A record is never split between partitions. Issue #1 adds no retention; journal growth is therefore unbounded until a separate retention design is implemented.

The offline `auth recover-bootstrap-api-key` command cannot call an Orleans grain. It opens the same journal implementation under the exclusive audit lock, writes its system attempt/outcome records, and exits. A running server prevents concurrent offline recovery by holding the lock.

## Integrity And Recovery

The journal validates:

- store and partition identity
- contiguous one-based partition numbers
- positive contiguous record sequences
- supported record schema
- required operation and phase fields
- operation UUID format
- canonical hash input
- the hash chain within and across partitions
- complete, nonblank, canonical UTF-8 JSON lines with no unknown members

Startup may truncate only a proven incomplete final line in the active partition. Malformed committed-prefix data, a sequence gap, identity mismatch, or hash mismatch is `DATA_LOSS`; the audit grain faults and readiness remains false. Earlier partitions are immutable after rollover.

Hash chaining detects accidental corruption and simple modification. It does not prevent an administrator with filesystem write access from rewriting the complete journal and recomputing unkeyed hashes. Tamper resistance requiring external signing, write-once storage, or remote attestation is future work.

## Durability And Failure Semantics

### Before Mutation

After authentication and authorization succeed, the server builds a sanitized audit context and durably appends `attempt` before invoking an audited service or actor. If append fails, the call returns `UNAVAILABLE`, no domain actor is invoked, readiness becomes false, and later audited operations continue to fail closed while the sink is faulted.

The attempt context is propagated as an immutable Orleans message. `ClaimsPrincipal`, HTTP types, authorization headers, tokens, and credentials never cross the actor boundary.

### Authoritative Outcome

The component that determines the durable or semantic outcome appends `outcome`:

- Database Directory records database creation completion.
- Schema and Handler actors record catalog publication, no-op, rejection, and removal outcomes.
- Decision records execute, prepare, and complete outcomes.
- Main supplies committed event ranges to Decision; the decision runtime emits a bounded conflict-retry counter.
- Index Orchestrator records awaited rebuild completion; State records one-way dispatch and the State actor records later completion/fault.
- `ApiKeyStore` callers record API-key and bootstrap outcomes without secret material.

Actor-side outcome recording means a client disconnect cannot turn a committed mutation into an apparently absent audit operation.

### Post-Commit Audit Failure

Audit persistence is not transactionally atomic with existing event, schema, handler, index, state, or API-key files. If a domain mutation commits and its outcome record then fails:

1. the call returns `UNAVAILABLE` rather than a false success,
2. readiness becomes false,
3. later audited mutations are rejected while the sink is faulted,
4. the durable attempt remains queryable as unresolved after audit recovery,
5. the caller reconciles through command ID or current domain/catalog state.

This is an explicitly ambiguous result, like an uncertain durable append. The implementation must never synthesize a successful outcome it did not persist. Automatic reconciliation for catalog and security mutations is outside issue #1.

An incomplete attempt does not by itself keep readiness false after restart. Process failure can occur between any attempt and outcome, and the journal preserves that fact rather than claiming whether the domain operation completed.

## Readiness

`GET /health/ready` remains anonymous and keeps its existing database and bootstrap checks. It additionally returns 503 while:

- the audit journal cannot initialize or recover,
- the journal lock cannot be acquired,
- committed audit data fails validation, or
- a runtime audit append has faulted the sink.

`GET /health/live` remains independent of audit health. The protected gRPC `GetHealth` response is not expanded in issue #1; audit status is exposed through `AuditService` and readiness.

## Audit API

The protocol has an eighth service and one RPC, for a total of 35 actions:

```protobuf
service AuditService {
  rpc ListAuditRecords(ListAuditRecordsRequest) returns (ListAuditRecordsResponse);
}
```

`ListAuditRecordsRequest` contains:

| Field | Rule |
|---|---|
| `after_sequence` | Exclusive cursor; `0` starts at the beginning. |
| `limit` | `0` selects the default `100`; otherwise the valid range is `1..1000`. |
| `database` | Optional exact normalized database filter. |
| `operation` | Optional exact stable operation filter. |
| `phase` | Optional exact phase filter. |
| `outcome` | Optional exact outcome filter. |
| `authentication_scheme` | Optional exact scheme filter. |
| `subject` | Optional exact subject filter. |

The response contains ordered records, `next_after_sequence`, and `has_more`. Filtered pagination advances by the last examined global sequence, not merely the last returned record, so a sparse filter always makes progress. A successful empty page can still advance the cursor.

Audit reads capture a committed journal boundary when the call starts. Records appended later appear only on a subsequent page. The API does not follow or subscribe; durable audit streaming is future work.

### Audit Permissions

Every audit call requires its normal derived action permission:

```text
grpc:nativedcb.v1.AuditService:ListAuditRecords
```

It additionally requires:

| Query | Required resource permission |
|---|---|
| Exact database filter | `audit:<encoded-database>:read` |
| No database filter | `audit:*:read` |
| Security, authorization, bootstrap, or global records | `audit:*:read` |

Wildcards follow the existing complete-segment permission rules. The server authorizes the database filter before reading the journal. A caller with one database grant cannot omit the filter or inspect global records.

## Client Surfaces

`NativeDcbClient` provides a paginated audit-list method. The CLI provides `audit list` with cursor, limit, and exact filter options and emits one protobuf JSON response per page. It does not automatically drain the entire journal.

The Web console has an Audit service card with the same bounded filters and explicit next-page control. It does not cache audit history in browser storage. `AuditService` is included in descriptor-derived coverage for all 35 methods.

## OpenTelemetry

NativeDCB uses OpenTelemetry for production logs, metrics, and traces. Exporter packages and registration belong only to `NativeDCB.Server`; Engine and Actors expose BCL `ActivitySource` and `Meter` instruments without depending on an exporter.

OTLP export is enabled when `OTEL_EXPORTER_OTLP_ENDPOINT` is configured. Standard OpenTelemetry environment variables configure protocol, headers, resource attributes, sampling, and SDK disablement:

```text
OTEL_SERVICE_NAME=NativeDCB.Server
OTEL_EXPORTER_OTLP_ENDPOINT=https://collector.example:4317
OTEL_EXPORTER_OTLP_PROTOCOL=grpc
OTEL_EXPORTER_OTLP_HEADERS=<deployment secret>
OTEL_TRACES_SAMPLER=parentbased_traceidratio
OTEL_TRACES_SAMPLER_ARG=0.1
OTEL_SDK_DISABLED=false
```

OTLP headers are deployment secrets and must never be logged or copied into audit records. Tests and existing benchmark baselines use `OTEL_SDK_DISABLED=true` unless a test listener is installed explicitly.

The server registers ASP.NET Core and runtime instrumentation plus the `NativeDCB.Server`, `NativeDCB.Actors`, and `NativeDCB.Engine` sources/meters. It exports structured `ILogger` records, but does not duplicate every durable audit record into application logs.

## Traces

Custom activities cover:

- complete local/remote decision execution
- authoritative and eventual query strategy/results
- event-store append and durable flush
- audit append and query

Trace attributes may include safe routing metadata such as database, handler, operation, consistency mode, query strategy, outcome, revision, and event counts. They must not include the sensitive values excluded above.

W3C trace context remains process/request correlation, not an authorization or durability boundary. Audit records copy the current trace ID only when present; trace sampling never suppresses audit persistence.

## Metrics

Implemented custom instruments include:

| Instrument | Type | Bounded dimensions |
|---|---|---|
| `nativedcb.audit.records` | counter | phase, category, outcome |
| `nativedcb.audit.failures` | counter | operation, fault kind |
| `nativedcb.audit.append.duration` | histogram | phase |
| `nativedcb.decisions` | counter | operation, outcome |
| `nativedcb.decision.duration` | histogram | operation, outcome |
| `nativedcb.decision.retries` | counter | operation |
| `nativedcb.events.committed` | counter | operation |
| `nativedcb.append.duration` | histogram | outcome |
| `nativedcb.queries` | counter | consistency, strategy, outcome |
| `nativedcb.query.duration` | histogram | consistency, strategy |
| `nativedcb.query.events` | histogram | strategy, role (`returned`) |
| `nativedcb.subscriptions.active` | up/down counter | kind |
| `nativedcb.subscription.events` | counter | kind |
| `nativedcb.maintenance.operations` | counter | kind, outcome |
| `nativedcb.maintenance.duration` | histogram | kind, outcome |
| `nativedcb.database.transitions` | counter | from state, to state |
| `nativedcb.authorization.denials` | counter | operation, status |

Database names, handler names, schema names, subjects, command IDs, API-key IDs, event types, trace IDs, and consistency-key values are forbidden metric dimensions. This prevents unbounded time-series cardinality. Such values belong only in protected traces or audit records when permitted.

## Structured Logging

Application logs remain operational diagnostics rather than the audit authority. Structured logs cover:

- audit persistence failures that activate fail-closed behavior
- denial-audit failures that cannot replace the security result
- unexpected service exceptions
- bootstrap credential lifecycle warnings without credential material in the structured message

Expected domain rejection, stale completion, permission denial, and normal cancellation are metrics/audit outcomes, not error logs. Unexpected exceptions continue to be sanitized for clients while retaining server-side exception details.

## Verification Coverage

Tests cover:

- audit sequences and hashes remain valid across append, rollover, and restart
- a proven incomplete active suffix is truncated but committed-prefix corruption fails recovery
- the exclusive lock prevents a second writer
- attempts are durable before actor invocation
- sink failure before mutation returns `UNAVAILABLE` and prevents mutation
- JWT issuer/subject and API-key ID identities are stable and correctly isolated
- credentials, payloads, signatures, key values, and exporter headers never appear in journal files or API responses
- denied calls are audited without changing their security status
- schema/handler no-op, incompatible, invalid, replacement, and removal outcomes are distinct
- every execute/prepare/complete decision outcome is represented
- local retry uses one public outcome and a bounded retry metric
- concurrent duplicate commands produce committed and already-committed records linked by command ID
- audit query permissions isolate exact databases and protect global/security records
- pagination is ordered, bounded, stable, and makes progress under sparse filters
- restart preserves sequence and query continuity
- readiness reflects bootstrap, database, and audit health independently
- Activity and Meter listeners observe expected names, outcomes, and bounded dimensions
- no telemetry listener/exporter leaves existing behavior unchanged
- native gRPC and gRPC-Web can query audit records
- SDK, CLI, and Web cover the 35-method descriptor surface

Unit tests exercise the journal directly with temporary directories. Server integration tests use deterministic time where required, real JWT/API-key identities, corruption/fail-closed checks, and restart-preserved roots. The process-level Commerce test launches the built server, creates audited state, restarts it, and verifies persisted sequence/hash continuity.

## Implemented Components

1. Versioned record, identity, operation context, configuration, and redaction rules.
2. Partitioned durable journal, hash chain, recovery, rollover, locking, and bounded reads.
3. Singleton Audit actor, exclusive runtime ownership, status, and readiness integration.
4. Denial recording and fail-closed pre-operation attempts in authorization flow.
5. Sanitized actor context and authoritative outcomes for database, catalog, statement, decision, administration, API-key, bootstrap, and recovery operations.
6. `AuditService`, permission enforcement, protocol mapping, SDK, CLI, Web, and descriptor coverage.
7. Engine/Actor BCL telemetry plus server OpenTelemetry logs, metrics, traces, and conditional OTLP export.
8. Journal, integration, gRPC-Web, listener, restart, redaction, corruption, and process-level verification.

## Future Work

The following require separate designs:

- retention, archival, compaction, and deletion policy
- externally signed or write-once tamper resistance
- external audit/SIEM sinks with delivery acknowledgement
- durable audit subscriptions or live tailing
- full statement/catalog history with source retention
- automatic reconciliation of unresolved catalog/security attempts
- audit backup/restore and cross-store consistency
- multi-silo audit sequencing and storage coordination
- Prometheus endpoint exposure and access policy
