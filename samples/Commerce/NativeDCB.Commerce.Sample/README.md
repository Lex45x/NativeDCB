# NativeDCB Commerce Sample

This executable drives the reusable `NativeDCB.Commerce` contracts and decisions through `NativeDcbClient`. It accepts:

```text
NativeDCB.Commerce.Sample [mode] [address] [database]
```

The defaults are `run`, `http://localhost:5010`, and `commerce-sample`. Start `NativeDCB.Server`, then run a mode from the repository root:

```powershell
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- seed
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- run http://localhost:5010 commerce-run
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- contention http://localhost:5010 commerce-contention
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- remote-payment http://localhost:5010 commerce-remote
dotnet run --project samples\Commerce\NativeDCB.Commerce.Sample -- recovery http://localhost:5010 commerce-recovery
```

Every mode first calls `CommerceSeeder.SeedAsync`. Seeding creates the database when needed and idempotently registers the shared schemas and handlers without appending commerce events. The scenarios use deterministic entity and command IDs, so use a separate database for each mode; use a fresh database when you want to observe every race again.

## Modes

| Mode | Behavior |
|---|---|
| `seed` | Seeds the catalog and exits. |
| `run` | Publishes and stocks a product, registers a customer, reserves and checks out a cart, redeems a coupon, authorizes payment, ships and delivers the order, receives a return, restocks it, and refunds the payment. It prints every semantic outcome. |
| `contention` | Races the last inventory unit, a reservation against checkout, a one-use coupon, and two submissions of one command ID. It reads committed events and prints nonnegative-stock, checkout-ordering, coupon-limit, and single-batch reconciliation invariants. |
| `remote-payment` | Prepares two payment capabilities over the same unpaid order, completes one from a proposed `PaymentAuthorized` event, and shows that the matching authorization makes the older capability stale. |
| `recovery` | Executes and replays a fixed command ID, then verifies the original event batch through `GetEventsByCommandId`. |

`run` executes `AuthorizePayment` and `RefundPayment` as local server-side decisions. This keeps the complete walkthrough usable when remote-decision HMAC support is not configured.

## Trusted Payment Worker

The `remote-payment` mode requires remote decisions to be enabled before the server starts:

```powershell
$env:RemoteDecisions__ActiveKeyId = "dev"
$env:RemoteDecisions__SigningKeys__dev = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8="
dotnet run --project src\NativeDCB.Server
```

Use a securely generated key outside local development. The returned model signature is an opaque bearer capability: do not decode, log, or expose it.

Preparation hydrates `PaymentPreparationModel`, but does not call a payment gateway or choose the event. A trusted worker must validate the model, perform the external authorization, and propose the exact schema-valid `PaymentAuthorized` event. Completion verifies the HMAC capability, command and plan identity, schema and emission shape, expiry, and whether matching state moved. It does not prove that the worker called a gateway or derived the event honestly. Protect capability holders as privileged payment workers and make gateway calls idempotent.

## Recovery Scope

The client sample cannot safely restart the server process that it is connected to. `recovery` demonstrates the client-visible replay and reconciliation contract against a running server. Durable stop/start recovery is covered by server integration tests. The Commerce process E2E test, `NativeDCB.EndToEndTests.Processes.CommerceSampleProcessTests`, owns a real server lifecycle and temporary storage while verifying sample seeding and the NDL, SDK, and remote publication paths.
