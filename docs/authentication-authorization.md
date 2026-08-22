# NativeDCB Authentication And Authorization

Status: implemented reference for [issue #2](https://github.com/Lex45x/NativeDCB/issues/2)  
Last verified: 2026-08-22

## Overview

NativeDCB authenticates callers with OIDC/OAuth 2.0 JWT access tokens or internally generated API keys. All 34 gRPC actions require action permissions, and handler operations additionally require database-and-handler permissions. Only `GET /health/live` and `GET /health/ready` are anonymous. `GrpcWeb:AllowedOrigins` remains a browser CORS policy, not an authentication boundary. The remote-decision `model_signature` remains an HMAC-protected decision capability, not caller authentication.

API-key authentication is the secure default. An empty store creates one random bootstrap key with `*:*:*` and keeps readiness false until the first normal key is created.

## Security Properties

The implementation:

- authenticate callers with externally issued OIDC/OAuth 2.0 JWT access tokens or NativeDCB-generated API keys
- require a distinct permission for every gRPC action
- support exact permissions and explicit wildcards
- add database-and-handler permissions to every handler operation
- keep only the HTTP liveness and readiness probes anonymous
- deny access before invoking an Orleans grain or mutating durable state
- preserve native gRPC and gRPC-Web support
- avoid placing access tokens or client secrets in static browser configuration, command-line arguments, logs, or error details

NativeDCB validates external access tokens and issues its own API keys for service clients. It does not issue JWTs, manage users, store passwords, implement an OAuth authorization server, or translate roles into permissions.

Audit records, tenant isolation, token revocation, and persisted handler provenance are separate requirements.

## Connection And Call Semantics

Native gRPC normally keeps one HTTP/2 connection open and multiplexes many RPCs over it. Connection reuse avoids repeated TCP and TLS handshakes, but authentication remains per RPC: every call carries its JWT or API key in gRPC metadata and is authorized independently.

A unary call is authorized before its service method executes. A server-streaming call is authorized once when the RPC starts; token expiry or permission revocation does not terminate an already established stream. Clients must reconnect with a current token after a stream ends. Future client-streaming and duplex methods will follow the same call-start rule.

NativeDCB does not add a stateful login RPC or connection-bound credential session. Such a session would behave poorly across reconnects, proxies, load balancers, and multiple server instances. Deployments that require connection-bound client identity can add mutual TLS at the transport or ingress layer, but mTLS is not part of this implementation.

## Authentication

Authentication supports ASP.NET Core JWT bearer validation against an external OIDC authority and internally generated API keys:

```json
{
  "Authentication": {
    "Providers": "JwtBearer,ApiKey",
    "JwtBearer": {
      "Authority": "https://identity.example",
      "Audience": "nativedcb",
      "RequireHttpsMetadata": true
    },
    "ApiKeys": {
      "StorePath": ".security/api_keys_v1.json",
      "BootstrapOnEmptyStore": true
    }
  }
}
```

At least one authentication provider is required. Missing or invalid configuration for an enabled provider fails startup rather than silently enabling anonymous access. `Providers` is a comma-delimited scalar so environment configuration replaces it atomically. Explicit `Providers=Disabled` is accepted only as the sole provider and is used by tests and existing benchmark baselines.

Each call presents exactly one credential scheme. JWT and API-key identities are not merged, and permissions from multiple credentials cannot be combined to satisfy one request.

JWT validation requires a trusted signature, expected issuer, expected audience, unexpired lifetime, and HTTPS authority metadata. Inbound credentials must be protected by HTTPS or an equivalently trusted encrypted ingress. If TLS terminates at a reverse proxy, forwarded-header trust must be configured narrowly for that proxy.

Permissions are read from standard space-delimited `scope` and `scp` claims. Claim type mapping is disabled so configured claim names remain stable. Permission comparison is ordinal.

### Internally Generated API Keys

API keys are intended for automation and service clients that cannot participate in an interactive OIDC flow. Browser applications should use OIDC authorization-code flow with PKCE instead of long-lived API keys.

NativeDCB generates both parts of a key with a cryptographically secure random number generator. A key has a public 128-bit identifier and a 256-bit secret in a versioned, transport-safe representation:

```text
ndcb1_<base64url-key-id>.<base64url-secret>
```

Clients send the complete value in gRPC metadata:

```text
authorization: ApiKey ndcb1_<key-id>.<secret>
```

The complete key is returned once when it is created and is never retrievable again. NativeDCB stores only the key identifier, a versioned SHA-256 digest of the generated high-entropy secret, label, permissions, creation time, optional expiry, revocation state, and non-secret operational metadata. Secret comparison is constant-time. The raw secret must never be persisted, logged, included in an error, accepted from a caller, or placed in command-line arguments.

The API-key catalog is server-wide rather than database-specific. Its versioned file defaults below `DatabaseRoot/.security/`; a relative `StorePath` is resolved against `DatabaseRoot`. The catalog is updated with atomic replacement and an exclusive writer lock. File permissions must restrict access to the server identity. This design follows the current single-silo boundary; coordinating an API-key catalog across multiple server instances is part of future multi-silo work.

Revocation and expiry are checked when each RPC starts. They do not terminate a stream that has already been authorized. Rotation creates a replacement key, deploys it to clients, and then revokes the old key. Revoked records remain as non-secret metadata until an explicit future retention policy removes them.

### API Key Bootstrap And Management

When the API-key provider is enabled, `BootstrapOnEmptyStore` defaults to `true`, and the store contains no keys, NativeDCB generates one special bootstrap API key with the exact permission `*:*:*`. It is a random per-installation credential, never a fixed, source-controlled, or well-known default value.

The complete bootstrap key is displayed once directly to the startup terminal. Only its digest is written to the key store. Deployments must capture that one-time output through a protected installation channel and must not retain it in general application logs. A restart does not print an existing bootstrap secret again.

The bootstrap record is marked separately from normal API keys. While an active bootstrap key exists, `/health/ready` reports not ready so an installation cannot silently enter service with its initial superuser credential. The bootstrap key can call every gRPC action because `*:*:*` covers every permission, including API-key creation.

The first successful creation of a non-bootstrap API key atomically revokes the bootstrap key. The operator must therefore create a long-lived administrative key first, then use that administrative key to create narrower workload keys. Rotation follows the standard create, deploy, and revoke sequence.

If the one-time bootstrap secret is lost before a normal key is created, NativeDCB provides a local recovery command. The command runs on the server host while the server is stopped, acquires the API-key store exclusively, revokes the lost bootstrap record, generates a replacement bootstrap key with `*:*:*`, stores only its digest, and displays the replacement once. The secret is output data and is never accepted as a command argument.

`BootstrapOnEmptyStore=false` disables automatic bootstrap generation. That setting is valid only when another configured authentication provider can authorize `CreateApiKey`; otherwise startup fails because no caller could establish first access.

After bootstrap, `AuthenticationService` manages keys through authenticated gRPC calls:

| RPC | Required gRPC permission | Required API-key resource permission |
|---|---|---|
| `CreateApiKey` | `grpc:nativedcb.v1.AuthenticationService:CreateApiKey` | `apikey:*:create` |
| `ListApiKeys` | `grpc:nativedcb.v1.AuthenticationService:ListApiKeys` | `apikey:*:list` |
| `RevokeApiKey` | `grpc:nativedcb.v1.AuthenticationService:RevokeApiKey` | `apikey:<key-id>:revoke` |

`CreateApiKey` accepts a label, permissions, and optional expiry, but never a caller-selected secret. Its response is the only network response that contains the generated complete key. `ListApiKeys` returns identifiers and non-secret metadata only. `RevokeApiKey` is idempotent and does not disclose a secret.

A caller can delegate only requested permissions that are subsets of its own effective permissions. Comparison is component-wise: an exact caller grant can delegate only that exact grant, while a caller wildcard can delegate exact or narrower wildcard patterns within the covered group/resource/action. This prevents a key with key-creation access from escalating its own authority. Automatic bootstrap generation and the local bootstrap recovery command are the only paths that can create `*:*:*` without an authenticated caller.

## Permission Grammar

Every permission has exactly three colon-separated segments:

```text
group:resource:action
```

Each segment can contain an exact value or the complete wildcard `*`. Partial globs such as `Execute*`, substring matching, and implicit permission inheritance are not supported.

Examples:

```text
grpc:nativedcb.v1.CommandService:ExecuteHandler
grpc:nativedcb.v1.CommandService:*
grpc:*:*
*:nativedcb.v1.CommandService:ExecuteHandler
*:*:*
```

`*:*:*` is an explicit superuser permission. A malformed permission never matches a required permission, and token contents must not be included in logs or denial messages.

### Handler Resources

Handler permissions use a composite resource:

```text
handler:<database>/<handler>:<action>
```

Database names are normalized with the same rules as storage routing. The normalized database and the case-sensitive handler name are independently encoded as canonical RFC 3986 path components before they are joined by `/`. This makes literal `/`, `:`, `%`, whitespace, Unicode, and `*` unambiguous. A literal asterisk in a name is encoded; only an unencoded complete `*` component is a wildcard.

Handler resource patterns support exact and database/handler wildcard combinations:

```text
handler:commerce/PublishProduct:execute
handler:commerce/*:execute
handler:*/PublishProduct:execute
handler:*/*:execute
handler:commerce/*:*
handler:*/*:*
```

The gRPC action permission and the handler permission are both required. A handler permission does not grant access to its RPC, and a gRPC permission does not grant access to a handler.

## gRPC Action Permissions

Every method derives its required permission from its canonical protobuf service and method names. This avoids a second, manually named action vocabulary.

| Service | RPC | Required permission |
|---|---|---|
| DatabaseService | `ListDatabases` | `grpc:nativedcb.v1.DatabaseService:ListDatabases` |
| DatabaseService | `CreateDatabase` | `grpc:nativedcb.v1.DatabaseService:CreateDatabase` |
| DatabaseService | `GetDatabaseInfo` | `grpc:nativedcb.v1.DatabaseService:GetDatabaseInfo` |
| DatabaseService | `GetHealth` | `grpc:nativedcb.v1.DatabaseService:GetHealth` |
| DatabaseService | `GetCapabilities` | `grpc:nativedcb.v1.DatabaseService:GetCapabilities` |
| DatabaseService | `GetHead` | `grpc:nativedcb.v1.DatabaseService:GetHead` |
| CatalogService | `RegisterEventSchema` | `grpc:nativedcb.v1.CatalogService:RegisterEventSchema` |
| CatalogService | `RegisterCommandSchema` | `grpc:nativedcb.v1.CatalogService:RegisterCommandSchema` |
| CatalogService | `RemoveSchema` | `grpc:nativedcb.v1.CatalogService:RemoveSchema` |
| CatalogService | `GetSchema` | `grpc:nativedcb.v1.CatalogService:GetSchema` |
| CatalogService | `ListSchemas` | `grpc:nativedcb.v1.CatalogService:ListSchemas` |
| CatalogService | `RegisterHandler` | `grpc:nativedcb.v1.CatalogService:RegisterHandler` |
| CatalogService | `RemoveHandler` | `grpc:nativedcb.v1.CatalogService:RemoveHandler` |
| CatalogService | `GetHandler` | `grpc:nativedcb.v1.CatalogService:GetHandler` |
| CatalogService | `ListHandlers` | `grpc:nativedcb.v1.CatalogService:ListHandlers` |
| CatalogService | `ValidateNdl` | `grpc:nativedcb.v1.CatalogService:ValidateNdl` |
| CommandService | `ExecuteHandler` | `grpc:nativedcb.v1.CommandService:ExecuteHandler` |
| CommandService | `PrepareDecision` | `grpc:nativedcb.v1.CommandService:PrepareDecision` |
| CommandService | `CompleteDecision` | `grpc:nativedcb.v1.CommandService:CompleteDecision` |
| CommandService | `GetEventsByCommandId` | `grpc:nativedcb.v1.CommandService:GetEventsByCommandId` |
| EventService | `ReadEventsByRange` | `grpc:nativedcb.v1.EventService:ReadEventsByRange` |
| EventService | `ReadEventsByQuery` | `grpc:nativedcb.v1.EventService:ReadEventsByQuery` |
| EventService | `ReadEventsByTypeAndKeys` | `grpc:nativedcb.v1.EventService:ReadEventsByTypeAndKeys` |
| EventService | `SubscribeEvents` | `grpc:nativedcb.v1.EventService:SubscribeEvents` |
| StatementService | `ExecuteStatement` | `grpc:nativedcb.v1.StatementService:ExecuteStatement` |
| StatementService | `ExplainStatement` | `grpc:nativedcb.v1.StatementService:ExplainStatement` |
| AdministrationService | `ListPartitions` | `grpc:nativedcb.v1.AdministrationService:ListPartitions` |
| AdministrationService | `ListIndexes` | `grpc:nativedcb.v1.AdministrationService:ListIndexes` |
| AdministrationService | `GetStateFileStatus` | `grpc:nativedcb.v1.AdministrationService:GetStateFileStatus` |
| AdministrationService | `RequestIndexRebuild` | `grpc:nativedcb.v1.AdministrationService:RequestIndexRebuild` |
| AdministrationService | `RequestStateRebuild` | `grpc:nativedcb.v1.AdministrationService:RequestStateRebuild` |
| AuthenticationService | `CreateApiKey` | `grpc:nativedcb.v1.AuthenticationService:CreateApiKey` |
| AuthenticationService | `ListApiKeys` | `grpc:nativedcb.v1.AuthenticationService:ListApiKeys` |
| AuthenticationService | `RevokeApiKey` | `grpc:nativedcb.v1.AuthenticationService:RevokeApiKey` |

The table includes all 34 implemented actions. There are no anonymous gRPC actions. `GetHealth` and `GetCapabilities` are protected like every other RPC. The HTTP `GET /health/live` and `GET /health/ready` probes remain anonymous and intentionally expose only minimal process/readiness state. The root HTTP endpoint requires an authenticated caller through the server fallback policy.

## Handler Permission Matrix

The following calls require a handler permission in addition to their gRPC permission:

| RPC | Required handler action | Resource source |
|---|---|---|
| `CatalogService.RegisterHandler` | `register` | Request database and handler name |
| `CatalogService.RemoveHandler` | `remove` | Request database and handler name |
| `CatalogService.GetHandler` | `read` | Request database and handler name |
| `CatalogService.ListHandlers` | `list` | Request database and handler wildcard (`<database>/*`) |
| `CommandService.ExecuteHandler` | `execute` | Request database and handler name |
| `CommandService.PrepareDecision` | `prepare` | Request database and handler name |
| `CommandService.CompleteDecision` | `complete` | Database and handler from the verified signed capability |
| `StatementService.ExecuteStatement` | `register` | Request database and every parsed decision name |

For example, executing `PublishProduct` in `commerce` requires both:

```text
grpc:nativedcb.v1.CommandService:ExecuteHandler
handler:commerce/PublishProduct:execute
```

Handler grants govern operations on registered handlers. They do not provide event confidentiality or general database isolation. Event reads, command reconciliation reads, schemas, database metadata, and administrative status remain controlled by their independent gRPC action permissions. In particular, `GetDatabaseInfo` can expose catalog names/fingerprints to callers granted that action.

## Authorization Order

Authorization runs before the gRPC service invokes any grain:

1. JWT bearer or API-key authentication validates the caller and supplies its permissions.
2. The exact gRPC permission is checked.
3. Request routing values needed for a handler resource are validated and normalized.
4. Any required handler permission is checked.
5. The service method and its grain calls are allowed to execute.

Missing or invalid credentials return gRPC `UNAUTHENTICATED`. A valid identity without a required permission returns `PERMISSION_DENIED`. Invalid request data retains its existing status after the caller has enough permission to reach that validation. A caller without a handler grant is denied before handler lookup, preventing handler-existence probing.

The authorization interceptor implements unary, server-streaming, client-streaming, and duplex interception. Unknown future RPCs still require their derived gRPC permission. Descriptor-based Web coverage ensures new RPCs cannot silently disappear from the client surface.

### CompleteDecision

`CompleteDecisionRequest` deliberately does not contain a trusted handler name. Its `model_signature` contains HMAC-authenticated database and handler claims produced by `PrepareDecision`.

After the `CompleteDecision` gRPC permission succeeds, the server verifies the capability, confirms its database matches the request, derives the handler resource from the signed claims, and requires `handler:<signed-database>/<signed-handler>:complete`. It never authorizes from a plaintext handler field. The actor path verifies the capability again before mutation as defense in depth.

Caller authentication and the remote-decision capability are independent. A valid JWT or API key does not replace `model_signature`, and a valid `model_signature` does not authenticate its bearer to the server API.

### ExecuteStatement

One valid NDL document can register several handlers atomically. After the `ExecuteStatement` gRPC permission succeeds, the server parses the document and requires a `register` handler permission for every decision name before publishing any registration.

If the document cannot be parsed, the existing statement diagnostics can be returned because no registration can occur. If any parsed handler is unauthorized, the complete request is denied and no handler is changed. Authorization must not occur after `PublishStatementAsync`, because that method durably replaces all valid registrations before returning its result.

## Client Requirements

### .NET SDK

`NativeDcbClient` accepts either a refresh-capable access-token provider or an API-key provider and adds the matching authorization metadata when each RPC starts. The underlying `GrpcChannel` remains persistent and reusable. Constructors that accept externally created generated clients remain available for applications that configure their own authenticated transport.

### CLI

The CLI supports an access-token file, API-key file, `NATIVEDCB_ACCESS_TOKEN`, and `NATIVEDCB_API_KEY`. Inline credential arguments are intentionally excluded because command arguments can appear in shell history and process listings. Local API-key bootstrap prints a newly generated key once, but remote commands never echo configured credentials. `UNAUTHENTICATED` and `PERMISSION_DENIED` map to the permission exit code.

### WebAssembly Console

The standalone browser application uses OIDC authorization-code flow with PKCE and an authorization message handler for gRPC-Web calls. Authority, client ID, audience, and requested scopes are public client configuration; client secrets, fixed access tokens, and API keys must never be placed in `wwwroot`.

Browser preflight remains anonymous. CORS must continue to permit the `Authorization` header, but allowed origin does not grant a permission.

### Samples And Benchmarks

The Commerce sample obtains an access token or API key from `NATIVEDCB_ACCESS_TOKEN` or `NATIVEDCB_API_KEY` rather than a positional argument. Its seeder needs database creation and catalog permissions plus handler registration grants; scenario modes also need their command, event, and exact handler grants.

Existing benchmark measurements run with explicit disabled authentication so token validation does not silently change their baseline. Authenticated transport overhead belongs in a separate benchmark profile.

## Verification Coverage

Tests cover:

- all 34 protobuf methods are present in the client surface and require derived gRPC permissions
- only the two HTTP probes allow anonymous access
- missing, malformed, expired, wrong-issuer, and wrong-audience tokens return `UNAUTHENTICATED`
- exact permissions and wildcards match only the documented segments
- case, prefix, suffix, substring, malformed, and non-canonical encodings do not match
- one RPC permission cannot invoke another RPC
- handler permissions isolate database and case-sensitive handler identity
- database, handler, action, resource, group, and superuser wildcard combinations behave as documented
- denied catalog, command, statement, and administration calls cause no durable mutation
- `ListHandlers` requires the database-wide handler list permission
- `ExecuteStatement` authorizes every handler before publishing any registration
- `CompleteDecision` uses signed claims and denies before append when the handler permission is missing
- native unary, native server-streaming, gRPC-Web unary, and gRPC-Web server-streaming calls propagate authentication correctly
- an established stream is authorized at call start and reconnects use a newly supplied token
- enabled invalid configuration fails startup and Production rejects disabled mode
- API-key generation uses the documented entropy, returns the secret once, and stores only its digest
- an empty enabled API-key store generates exactly one random bootstrap key with `*:*:*`
- an active bootstrap key keeps readiness false and is revoked atomically when the first normal key is created
- bootstrap restart and recovery behavior never redisplays or persists an existing raw secret
- disabling bootstrap without another viable authentication provider fails startup
- malformed, unknown, expired, and revoked API keys return `UNAUTHENTICATED`
- API-key creation cannot delegate permissions broader than the caller possesses
- API-key listing never returns a digest or secret, and revocation is effective for the next RPC
- concurrent creation/revocation preserves a valid atomic catalog and exclusive-writer behavior
- SDK, CLI, Web, and sample credential handling does not disclose credentials

Most authorization combinations can use a test authentication scheme in `NativeDCB.Server.IntegrationTests`. At least one test must exercise real JWT validation and the API-key suite must exercise the real key store and authenticator. Process-level tests must explicitly select their authentication providers rather than relying on an implicit anonymous default.

## Implemented Components

1. Validated authentication-provider options, JWT bearer middleware, secure fallback policy, and anonymous probe metadata.
2. Permission parsing, canonical handler resources, wildcard matching, and delegation checks.
3. Versioned API-key storage, first-start bootstrap/recovery, readiness guard, and constant-time credential verification.
4. `AuthenticationService` key creation, non-secret listing, revocation, and non-escalating delegation.
5. Fail-closed gRPC action authorization and request-based handler checks.
6. Signed-capability resource extraction for `CompleteDecision` and parse-before-publish checks for `ExecuteStatement`.
7. SDK, CLI, Web, Commerce sample, test-host, and benchmark credential behavior.
8. Native gRPC, gRPC-Web descriptor, JWT, API-key, handler, statement, completion, and process coverage.
