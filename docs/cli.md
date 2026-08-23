# NativeDCB CLI

Status: implemented 35-RPC native gRPC client reference
Last verified: 2026-08-22

## Build And Run

Build the executable from the repository root:

```powershell
dotnet build src\NativeDCB.Cli
```

Run it through the project:

```powershell
dotnet run --project src\NativeDCB.Cli -- --help
dotnet run --project src\NativeDCB.Cli -- database list
```

The built executable is named `nativedcb` (`nativedcb.exe` on Windows). Examples below use `dotnet run` so they do not depend on an installation or `PATH` entry.

The CLI is a native gRPC client and covers all 35 RPCs in `nativedcb.v1`. Start `NativeDCB.Server` first. Its native HTTP/2 launch endpoint is `http://localhost:5010`; gRPC-Web is for browser clients and is not needed by the CLI.

## Server Selection

`--server URL` is a global option and may appear anywhere in the command line. Resolution order is:

1. `--server URL`
2. `NATIVEDCB_SERVER`
3. `http://localhost:5010`

The URL must be an absolute `http://` or `https://` URL. For example:

```powershell
$env:NATIVEDCB_SERVER = "http://localhost:5010"
dotnet run --project src\NativeDCB.Cli -- database health

dotnet run --project src\NativeDCB.Cli -- database health --server https://localhost:7154
```

HTTPS requires the server certificate to be trusted by the machine running the CLI.

## Credentials

Every RPC requires authentication unless the server explicitly runs with authentication disabled. Configure exactly one credential:

1. `--access-token-file PATH`, then `NATIVEDCB_ACCESS_TOKEN`
2. `--api-key-file PATH`, then `NATIVEDCB_API_KEY`

File options take precedence over the corresponding environment variables. The CLI trims surrounding whitespace and adds `Authorization: Bearer <token>` or `Authorization: ApiKey <key>` when each RPC starts. Inline credential arguments are intentionally unsupported so secrets do not appear in shell history or process listings.

```powershell
$env:NATIVEDCB_API_KEY = "<API key>"
dotnet run --project src\NativeDCB.Cli -- database health

dotnet run --project src\NativeDCB.Cli -- `
  --access-token-file .\access-token.txt database list
```

## Commands

Use `nativedcb <group> <command> --help` for command-specific usage.

### DatabaseService (6)

| Command | Inputs |
|---|---|
| `database list` | None. |
| `database create` | `--database NAME` |
| `database info` | `--database NAME` |
| `database health` | None. |
| `database capabilities` | None. |
| `database head` | `--database NAME` |

### CatalogService (10)

| Command | Inputs |
|---|---|
| `catalog register-event-schema` | `--database NAME --name SCHEMA`, exactly one schema input, optional `--allow-incompatible` |
| `catalog register-command-schema` | `--database NAME --name SCHEMA`, exactly one schema input, optional `--allow-incompatible` |
| `catalog remove-schema` | `--database NAME --name SCHEMA --kind event|command` |
| `catalog get-schema` | `--database NAME --name SCHEMA --kind event|command` |
| `catalog list-schemas` | `--database NAME`, optional `--kind event|command` |
| `catalog register-handler` | `--database NAME --name HANDLER --command-type TYPE`, NDL source and/or plan JSON, optional `--allow-incompatible` |
| `catalog remove-handler` | `--database NAME --name HANDLER` |
| `catalog get-handler` | `--database NAME --name HANDLER`, optional `--include-plan` and `--generate-ndl` |
| `catalog list-handlers` | `--database NAME` |
| `catalog validate-ndl` | `--database NAME`, exactly one NDL input, optional repeatable transient schemas |

Schema inputs are `--schema JSON`, `--schema-file PATH`, or `--schema-stdin`. Handler NDL uses `--source`, `--source-file`, or `--source-stdin`; plan JSON uses the corresponding `--plan` variants. At least one handler source or plan is required, and both may be supplied. Transient validation schemas are repeatable `--transient event:name=JSON`, `--transient-file command:name=PATH`, or `--transient-stdin event:name` options; either `event` or `command` is valid in each form.

### CommandService (4)

| Command | Inputs |
|---|---|
| `command execute-handler` | `--database NAME --handler HANDLER`, exactly one command JSON input, optional `--command-id UUID` |
| `command prepare-decision` | `--database NAME --handler HANDLER`, exactly one command JSON input, optional `--command-id UUID` |
| `command complete-decision` | `--database NAME`, exactly one signature input and exactly one proposed-events JSON input |
| `command events-by-command-id` | `--database NAME --command-id UUID` |

Command JSON uses `--command JSON`, `--command-file PATH`, or `--command-stdin`. Completion uses `--events JSON`, `--events-file PATH`, or `--events-stdin`; its JSON shape is `[{"type":"EventType","data":{...}}]`. The signature input is `--signature BASE64`, `--signature-file PATH`, or `--signature-stdin`. These options are mutually exclusive and accept the base64 protobuf-JSON value returned as `prepared.modelSignature` by preparation; surrounding whitespace is ignored.

Remote decisions are optional server functionality. Both remote commands receive `FAILED_PRECONDITION` unless `RemoteDecisions` is configured. Treat the signature as an opaque bearer capability: do not decode, log, or expose it. The CLI considers `prepared`, `committed`, and either method's `already_committed` outcome successful as applicable; stale, expired, invalidated, and failed outcomes exit `65`. It does not automatically prepare again or retry completion.

### EventService (4)

| Command | Inputs |
|---|---|
| `event read-range` | `--database NAME`, optional `--after ID --through ID --limit N --mode snapshot|follow` |
| `event read-query` | `--database NAME`, one query form, optional bounds and `--consistency eventual-index|committed-scan` |
| `event read-type-and-keys` | `--database NAME --event-type TYPE`, repeatable `--key name=value`, optional bounds and consistency |
| `event subscribe` | `--database NAME`, optional `--after ID`, query filters, and repeatable subscription-wide `--key name=value` |

`--after` defaults to `0` and is exclusive. `--through` is inclusive and must be greater than `--after`. `--limit` must be between `1` and `2147483647`. Range reads default to `snapshot`; query reads default to `committed-scan`. The short consistency aliases `eventual` and `committed` are also accepted.

### StatementService (2)

| Command | Inputs |
|---|---|
| `statement execute` | `--database NAME`, exactly one NDL input, optional `--allow-incompatible` |
| `statement explain` | `--database NAME`, exactly one NDL input |

NDL uses `--ndl TEXT`, `--ndl-file PATH`, or `--ndl-stdin`.

### AdministrationService (5)

| Command | Inputs |
|---|---|
| `admin list-partitions` | `--database NAME` |
| `admin list-indexes` | `--database NAME` |
| `admin state-file-status` | `--database NAME --partition NUMBER` |
| `admin rebuild-index` | `--database NAME --event-type TYPE`, optional repeatable `--key name=value` |
| `admin rebuild-state` | `--database NAME --partition NUMBER` |

### AuthenticationService (3)

| Command | Inputs |
|---|---|
| `auth create-api-key` | `--label TEXT`, repeatable `--permission SCOPE`, optional `--expires ISO-8601` |
| `auth list-api-keys` | None. |
| `auth revoke-api-key` | `--key-id ID` |

`create-api-key` returns the generated credential only in its creation response; listing and revocation return metadata without the secret. The server rejects grants that exceed the caller's own permissions. Store the returned key in an appropriate secret store and remove redirected response files after use.

### AuditService (1)

| Command | Inputs |
|---|---|
| `audit list` | Optional `--after SEQUENCE --limit N --database NAME --operation NAME --phase NAME --outcome NAME --authentication-scheme NAME --subject NAME` |

`--after` is an exclusive non-negative audit sequence and defaults to `0`. Omitting `--limit` sends `0`, selecting the server default of 100; an explicit limit must be between 1 and 1000. Every filter is an exact match. The command writes one `ListAuditRecordsResponse` protobuf JSON object and does not automatically drain later pages. Pass the response's `nextAfterSequence` to a subsequent `--after` invocation when `hasMore` is true.

The caller needs `grpc:nativedcb.v1.AuditService:ListAuditRecords` and either `audit:<encoded-database>:read` for an exact `--database` filter or `audit:*:read` when the filter is omitted.

## Input Conventions

### JSON And NDL

Each named JSON or text input accepts exactly one inline, file, or standard-input variant. JSON is parsed locally before the RPC is sent. `--query` is protobuf JSON for a complete `Query`; `--query-item` is protobuf JSON for one `QueryItem`.

Only one input option in a command may consume standard input. This matters for operations such as handler registration or NDL validation that can accept multiple independently sourced documents.

PowerShell examples:

```powershell
Get-Content -Raw .\command.json |
  dotnet run --project src\NativeDCB.Cli -- command execute-handler `
    --database commerce-cli --handler PublishProduct --command-stdin

dotnet run --project src\NativeDCB.Cli -- statement explain `
  --database commerce-cli --ndl-file samples\Commerce\NativeDCB.Commerce\NativeCommerce.ndl
```

### Keys

Repeat `--key name=value` where a command accepts keys. The first `=` separates the name from the value, so values may be empty or contain additional `=` characters. Key names must be non-empty. For example:

```powershell
--key sku=native-dcb-mug --key token=part1=part2 --key optional=
```

### Queries

`event read-query` requires a query. Build it in one or more of these ways:

- `--query JSON`, `--query-file PATH`, or `--query-stdin` for a complete protobuf `Query`
- repeatable `--query-item JSON` values, each adding one ORed item
- repeatable `--event-type TYPE` and `--key name=value`, which build one query item

These forms can be combined; each added item is ORed according to the protocol query semantics. Within the convenience item, event types are ORed and keys are ANDed.

`event subscribe` accepts the same complete-query and item forms. Its convenience item uses `--event-type` plus `--query-key name=value`; `--key name=value` instead adds a subscription-wide AND filter.

```powershell
dotnet run --project src\NativeDCB.Cli -- event read-query `
  --database commerce-cli `
  --event-type ProductPublished `
  --key sku=native-dcb-mug `
  --consistency committed-scan
```

## Streaming And Cancellation

The four EventService calls and `statement execute` are server-streaming commands. Finite reads and statements finish when the server closes the stream. `event read-range --mode follow` and `event subscribe` remain connected for live events until a limit is reached where supported, the server closes the stream, or the user presses `Ctrl+C`.

`Ctrl+C` cancels the active RPC, writes `cancelled` to standard error, and exits with code `130`. Stream items already written to standard output remain available to a pipeline or file.

## Output And Exit Codes

Unary success writes one compact protobuf JSON object followed by a newline to standard output. Streams write JSONL, one protobuf JSON object per line as each item arrives. Diagnostics and command outcomes remain in their protocol response objects. Usage, input, transport, and cancellation messages go to standard error; a NativeDCB `ErrorDetail` trailer is printed as JSON when present.

| Code | Meaning |
|---:|---|
| `0` | RPC completed and its application-level result was successful. |
| `64` | Invalid command, option, or option combination. |
| `65` | Invalid JSON/application input, a mapped data RPC status, or an unsuccessful application result such as validation failure, command rejection, failed statement completion, or rejected rebuild request. |
| `69` | Server unavailable or deadline exceeded. |
| `70` | Other RPC status or unexpected software error. |
| `74` | File or standard-input I/O error. |
| `77` | Unauthenticated or permission-denied RPC status. |
| `130` | Cancelled, including `Ctrl+C`. |

For streaming commands, response lines can be emitted before a later unsuccessful completion or transport error determines the final nonzero exit code.

## Remote Decision Flow

Seed the Commerce catalog first. Seeding creates the database and registers `PublishProductSdk` without publishing events:

```powershell
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- `
  seed http://localhost:5010 commerce-cli
```

Prepare a new product publication using the command input syntax shared with `execute-handler`:

```powershell
dotnet run --project src\NativeDCB.Cli -- command prepare-decision `
  --database commerce-cli --handler PublishProductSdk `
  --command '{"Sku":"remote-product","Name":"Remote Product","UnitPriceMinor":2500,"Currency":"USD"}' `
  --command-id 00000000-0000-0000-0000-000000000001 > .\prepared.json
```

Inspect the base64-encoded hydrated model before proposing an event. `ProductExists` must not be true for this publication to be valid. Then write the single event declared by the handler; the server validates its schema and derives the `sku` consistency key, so event JSON does not include keys.

```powershell
$prepared = Get-Content -Raw .\prepared.json | ConvertFrom-Json
$modelJson = [Text.Encoding]::UTF8.GetString(
  [Convert]::FromBase64String($prepared.prepared.modelJson))
$model = $modelJson | ConvertFrom-Json
if ($model.ProductExists -eq $true) {
  throw "Product already exists; do not propose ProductPublished."
}

$prepared.prepared.modelSignature | Set-Content .\model-signature.txt
'[{"type":"ProductPublished","data":{"Sku":"remote-product","Name":"Remote Product","UnitPriceMinor":2500,"Currency":"USD"}}]' |
  Set-Content .\events.json

dotnet run --project src\NativeDCB.Cli -- command complete-decision `
  --database commerce-cli --signature-file .\model-signature.txt --events-file .\events.json
```

Prefer `--signature-file` or `--signature-stdin` over inline `--signature` so the bearer capability does not appear in shell history, process listings, or command logs. For a pipeline, send only the extracted `prepared.modelSignature` to `--signature-stdin`; standard input cannot also supply events in the same invocation. Protect and delete redirected preparation and signature files when they are no longer needed.

Completion is a single conditional append. A `stale` response means a matching event was committed after preparation; prepare again with the same command ID and recompute the proposal if the command should continue. Unrelated head movement does not make the signed matching query stale.

## Practical Flow

Start the server's native HTTP/2 endpoint before this sequence. Seed the Native Commerce catalog; seeding creates the database when absent and idempotently registers all generated schemas, NDL handlers, and fluent SDK handlers without publishing events:

```powershell
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- `
  seed http://localhost:5010 commerce-cli
```

The exact sample NDL can then be explained through the CLI:

```powershell
dotnet run --project src\NativeDCB.Cli -- statement explain `
  --database commerce-cli `
  --ndl-file samples\Commerce\NativeDCB.Commerce\NativeCommerce.ndl
```

Execute `PublishProduct` with inline command JSON:

```powershell
$command = '{"Sku":"native-dcb-mug","Name":"Native DCB Mug","UnitPriceMinor":2500,"Currency":"USD"}'
dotnet run --project src\NativeDCB.Cli -- command execute-handler `
  --database commerce-cli --handler PublishProduct --command $command
```

Read the committed event by range or by its consistency key:

```powershell
dotnet run --project src\NativeDCB.Cli -- event read-range `
  --database commerce-cli --after 0 --mode snapshot

dotnet run --project src\NativeDCB.Cli -- event read-type-and-keys `
  --database commerce-cli --event-type ProductPublished --key sku=native-dcb-mug `
  --consistency committed-scan
```

For complete RPC behavior and limitations, see [NativeDCB gRPC API](grpc-api.md).
