# Native Commerce Domain

Status: implemented domain, sample, fixture, and benchmark workload reference for issue #8
Last verified: 2026-08-21

## Purpose

This document specifies **Native Commerce**, the e-commerce domain shared by the NativeDCB sample application, correctness tests, microbenchmarks, and real-server system benchmarks.

The domain is intentionally broader than a minimal demonstration. It covers catalog management, warehouse inventory, carts, checkout, promotions, payment, fulfilment, returns, subscriptions, and operational reads. Its workflows are designed to exercise NativeDCB's actual Dynamic Consistency Boundary semantics without implying aggregate streams, arbitrary projections, or transaction features that NativeDCB does not implement.

## Goals

1. Demonstrate how a substantial application can define consistency boundaries over one globally ordered event sequence.
2. Provide deterministic contracts and fixtures that can be reused by samples, tests, and benchmarks.
3. Exercise local decisions, remote prepare/complete, atomic multi-event batches, command reconciliation, conflict retries, indexed reads, committed reads, subscriptions, partition rollover, state files, and recovery.
4. Provide uniform, skewed, and deliberately contended workloads without changing domain semantics between benchmark layers.
5. Keep every command and event representable by the current schema, NDL, SDK, and gRPC capabilities.

## Non-Goals

- The domain is not a production commerce reference architecture.
- Entity identifiers do not define event streams or aggregate partitions.
- The sample does not implement authentication, authorization, taxation, accounting, fraud detection, or external gateway reliability.
- Benchmark workflows do not claim distributed transactions or multi-silo support.
- Remote completion is not treated as server-side enforcement of an external worker's business policy.

## NativeDCB Modeling Rules

The commerce database is one global event sequence. Event type and named consistency keys select the events needed by each decision. Query items are unioned, event types within an item are unioned, and all keys within an item must match one event.

The domain follows these rules:

1. Identifiers are non-empty strings with stable ordinal representation.
2. Money is represented as integer minor units plus a three-letter currency code. Floating-point money is forbidden.
3. Quantities are positive `long` values unless a command explicitly represents a signed adjustment.
4. Consistency keys are direct scalar event properties; nested keys are not used.
5. Every command receives a caller-generated UUID. Reusing it is allowed only when intentionally reconciling the same logical command.
6. Events carry the identifiers needed for every decision and read boundary that consumes them. There are no hidden aggregate-stream identifiers.
7. Commands that need a variable number of emissions are decomposed into per-line or per-item commands because NDL does not support loops or dynamic emission counts.

## Consistency-Key Vocabulary

| Logical key | Event property | Meaning |
|---|---|---|
| `sku` | `Sku` | Product and stock-keeping unit boundary |
| `warehouse` | `WarehouseId` | Warehouse boundary |
| `receipt` | `ReceiptId` | Inventory receipt idempotency boundary |
| `adjustment` | `AdjustmentId` | Inventory adjustment boundary |
| `customer` | `CustomerId` | Customer boundary |
| `cart` | `CartId` | Shopping-cart boundary |
| `line` | `LineId` | Cart or order line identity |
| `order` | `OrderId` | Order boundary |
| `coupon` | `CouponCode` | Coupon definition and global usage boundary |
| `payment` | `PaymentId` | Payment-attempt boundary |
| `shipment` | `ShipmentId` | Shipment boundary |
| `return` | `ReturnId` | Return boundary |
| `refund` | `RefundId` | Refund boundary |

Key names are part of the persistent schema contract. Renaming a key is an incompatible schema change and requires explicit override.

## Domain Areas

### Catalog

Catalog decisions create products, change prices, and discontinue products. Product state is reconstructed from events keyed by `sku`.

| Event | Required fields | Consistency keys |
|---|---|---|
| `ProductPublished` | `Sku`, `Name`, `UnitPriceMinor`, `Currency` | `sku` |
| `ProductPriceChanged` | `Sku`, `UnitPriceMinor`, `Currency` | `sku` |
| `ProductDiscontinued` | `Sku`, `Reason` | `sku` |

### Inventory

Inventory is tracked per SKU and warehouse. Reservations are made when individual cart lines are added, not during checkout. This permits deterministic per-line emissions and creates a precise contention boundary.

| Event | Required fields | Consistency keys |
|---|---|---|
| `InventoryReceived` | `ReceiptId`, `Sku`, `WarehouseId`, `Quantity` | `receipt`, `sku`, `warehouse` |
| `InventoryAdjusted` | `AdjustmentId`, `Sku`, `WarehouseId`, `QuantityDelta`, `Reason` | `adjustment`, `sku`, `warehouse` |
| `InventoryReserved` | `Sku`, `WarehouseId`, `CartId`, `LineId`, `Quantity` | `sku`, `warehouse`, `cart`, `line` |
| `InventoryReleased` | `Sku`, `WarehouseId`, `CartId`, `LineId`, `Quantity`, `Reason` | `sku`, `warehouse`, `cart`, `line` |
| `ReturnedInventoryRestocked` | `ReturnId`, `Sku`, `WarehouseId`, `Quantity` | `return`, `sku`, `warehouse` |

Available inventory is derived as received quantity plus signed adjustments and returned stock, minus reservations, plus releases. Workloads must never infer availability from the global head alone.

### Customers And Carts

| Event | Required fields | Consistency keys |
|---|---|---|
| `CustomerRegistered` | `CustomerId`, `DisplayName` | `customer` |
| `CartOpened` | `CartId`, `CustomerId`, `Currency` | `cart`, `customer` |
| `CartLineReserved` | `CartId`, `LineId`, `CustomerId`, `Sku`, `WarehouseId`, `Quantity`, `UnitPriceMinor`, `LineTotalMinor`, `Currency` | `cart`, `line`, `customer`, `sku`, `warehouse` |
| `CartLineRemoved` | `CartId`, `LineId`, `CustomerId`, `Sku`, `WarehouseId`, `Quantity`, `LineTotalMinor`, `Currency` | `cart`, `line`, `customer`, `sku`, `warehouse` |

`LineId` is never reused within a cart. Removing and then re-adding a product requires a new line ID. This makes duplicate command and duplicate-line behavior unambiguous.

### Checkout And Orders

| Event | Required fields | Consistency keys |
|---|---|---|
| `CartCheckedOut` | `CartId`, `OrderId`, `CustomerId`, `LineCount`, `GrossTotalMinor`, `Currency` | `cart`, `order`, `customer` |
| `OrderPlaced` | `OrderId`, `CartId`, `CustomerId`, `LineCount`, `GrossTotalMinor`, `Currency` | `order`, `cart`, `customer` |
| `OrderCancelled` | `OrderId`, `CustomerId`, `Reason` | `order`, `customer` |

Checkout summarizes the current reserved lines and atomically creates the order. Inventory is already reserved, so checkout does not dynamically emit inventory events.

### Promotions

| Event | Required fields | Consistency keys |
|---|---|---|
| `CouponDefined` | `CouponCode`, `DiscountMinor`, `Currency`, `MaximumGlobalUses`, `MaximumUsesPerCustomer` | `coupon` |
| `CouponRedeemed` | `CouponCode`, `CustomerId`, `OrderId` | `coupon`, `customer`, `order` |
| `OrderDiscountApplied` | `OrderId`, `CouponCode`, `CustomerId`, `DiscountMinor`, `Currency` | `order`, `coupon`, `customer` |

Global usage and per-customer usage are separate query branches. Two customers may contend on a coupon's global limit while unrelated coupons remain independent.

### Payments

| Event | Required fields | Consistency keys |
|---|---|---|
| `PaymentAuthorized` | `PaymentId`, `OrderId`, `AmountMinor`, `Currency`, `GatewayReference` | `payment`, `order` |
| `PaymentDeclined` | `PaymentId`, `OrderId`, `Reason` | `payment`, `order` |
| `PaymentRefunded` | `RefundId`, `PaymentId`, `OrderId`, `ReturnId`, `AmountMinor`, `Currency`, `GatewayReference` | `refund`, `payment`, `order`, `return` |

Payment authorization is the primary remote-decision example. The external worker is trusted to use the prepared model and return the declared event type. NativeDCB validates the signed context, catalog fingerprints, schema, event order, and staleness, but does not prove that the worker contacted a gateway or calculated the amount correctly.

### Fulfilment And Returns

| Event | Required fields | Consistency keys |
|---|---|---|
| `ShipmentCreated` | `ShipmentId`, `OrderId`, `CustomerId`, `Carrier` | `shipment`, `order`, `customer` |
| `OrderShipped` | `ShipmentId`, `OrderId`, `CustomerId`, `TrackingCode` | `shipment`, `order`, `customer` |
| `OrderDelivered` | `ShipmentId`, `OrderId`, `CustomerId` | `shipment`, `order`, `customer` |
| `ReturnRequested` | `ReturnId`, `OrderId`, `LineId`, `CustomerId`, `Sku`, `Quantity`, `Reason` | `return`, `order`, `line`, `customer`, `sku` |
| `ReturnReceived` | `ReturnId`, `OrderId`, `LineId`, `CustomerId`, `Sku`, `WarehouseId`, `Quantity` | `return`, `order`, `line`, `customer`, `sku`, `warehouse` |

Returns are processed per line. Receiving a return may atomically emit `ReturnReceived` and `ReturnedInventoryRestocked`. Refund authorization remains a separate remote or local payment command.

## Command And Decision Catalog

| Handler | Command | Main model inputs | Emissions | Primary authoring path |
|---|---|---|---|---|
| `PublishProduct` | `PublishProduct` | Product existence by SKU | `ProductPublished` | NDL and SDK |
| `ChangeProductPrice` | `ChangeProductPrice` | Product existence, active state, current currency | `ProductPriceChanged` | NDL |
| `DiscontinueProduct` | `DiscontinueProduct` | Product existence and active state | `ProductDiscontinued` | NDL |
| `ReceiveInventory` | `ReceiveInventory` | Product active state, receipt existence | `InventoryReceived` | NDL and SDK |
| `AdjustInventory` | `AdjustInventory` | Product state, current available stock, adjustment existence | `InventoryAdjusted` | NDL |
| `RegisterCustomer` | `RegisterCustomer` | Customer existence | `CustomerRegistered` | NDL |
| `OpenCart` | `OpenCart` | Customer existence, cart existence | `CartOpened` | NDL |
| `ReserveCartLine` | `ReserveCartLine` | Cart ownership/state, product price/state, stock, line history | `CartLineReserved`, `InventoryReserved` | NDL and SDK |
| `RemoveCartLine` | `RemoveCartLine` | Cart state and current line state | `CartLineRemoved`, `InventoryReleased` | NDL |
| `CheckoutCart` | `CheckoutCart` | Cart owner/state, active lines, totals, proposed order existence | `CartCheckedOut`, `OrderPlaced` | NDL and SDK |
| `DefineCoupon` | `DefineCoupon` | Coupon existence | `CouponDefined` | NDL |
| `RedeemCoupon` | `RedeemCoupon` | Coupon limits, customer usage, order/payment state | `CouponRedeemed`, `OrderDiscountApplied` | NDL |
| `AuthorizePayment` | `AuthorizePayment` | Order total/discount/state and prior payment | `PaymentAuthorized` | Remote decision |
| `RecordPaymentDeclined` | `RecordPaymentDeclined` | Order/payment state | `PaymentDeclined` | NDL |
| `CancelOrder` | `CancelOrder` | Payment, cancellation, and shipment state | `OrderCancelled` | NDL |
| `CreateShipment` | `CreateShipment` | Order, payment, cancellation, and shipment state | `ShipmentCreated` | NDL |
| `ShipOrder` | `ShipOrder` | Shipment and order state | `OrderShipped` | NDL and SDK |
| `DeliverOrder` | `DeliverOrder` | Shipment state | `OrderDelivered` | NDL |
| `RequestReturn` | `RequestReturn` | Delivered order and prior line return | `ReturnRequested` | NDL |
| `ReceiveReturn` | `ReceiveReturn` | Return state and receipt state | `ReturnReceived`, `ReturnedInventoryRestocked` | NDL |
| `RefundPayment` | `RefundPayment` | Return, payment, and prior refund state | `PaymentRefunded` | Remote or local decision |

## Core Decision Boundaries

### Reserve Cart Line

`ReserveCartLine` is the central contention workload. Its authoritative query includes:

| Purpose | Event types | Keys |
|---|---|---|
| Cart identity and owner | `CartOpened`, `CartCheckedOut` | `cart` |
| Product state and price | `ProductPublished`, `ProductPriceChanged`, `ProductDiscontinued` | `sku` |
| Warehouse availability | `InventoryReceived`, `InventoryAdjusted`, `InventoryReserved`, `InventoryReleased`, `ReturnedInventoryRestocked` | `sku`, `warehouse` |
| Line uniqueness | `CartLineReserved`, `CartLineRemoved` | `cart`, `line` |

The decision requires:

1. Positive quantity.
2. Existing cart owned by the command customer.
3. Cart not checked out.
4. Existing active product.
5. Unused line ID.
6. Available inventory greater than or equal to requested quantity.

It computes `LineTotalMinor = UnitPriceMinor * Quantity` and atomically emits `CartLineReserved` followed by `InventoryReserved`.

### Checkout Cart

`CheckoutCart` replays `CartOpened`, `CartLineReserved`, `CartLineRemoved`, and `CartCheckedOut` by `cart`, plus `OrderPlaced` by proposed `order`.

It requires the correct owner, an open cart, at least one active line, and an unused order ID. It atomically emits `CartCheckedOut` and `OrderPlaced`, preserving line count, gross total, and currency in both events.

### Redeem Coupon

`RedeemCoupon` combines:

- Coupon definition and all redemptions by `coupon` for the global limit.
- Redemptions by `coupon` and `customer` for the per-customer limit.
- Order placement, discounts, cancellation, and payment by `order`.

It atomically emits `CouponRedeemed` and `OrderDiscountApplied`. A coupon may not reduce the payable amount below zero.

### Authorize Payment

Preparation reads order placement, discounts, cancellation, prior authorization, and prior decline by `order`, and prior attempts by `payment`. The prepared model includes order existence, gross total, discount total, payable amount, currency, cancellation state, and prior payment state.

Completion accepts exactly one `PaymentAuthorized` event. A matching order or payment event after preparation makes completion stale. An unrelated product, cart, or order event does not.

## Domain Invariants

The sample and correctness verifier enforce these invariants:

1. A SKU is published once and cannot be priced or stocked before publication.
2. Available inventory for a SKU/warehouse cannot become negative through reservation decisions.
3. A cart belongs to one customer and uses one currency.
4. A line ID is used at most once within a cart.
5. A removed line releases exactly the quantity reserved by that line.
6. Checkout happens once and creates exactly one order in the same committed batch.
7. An order's gross total equals the sum of active cart-line totals at checkout.
8. Coupon limits cannot be exceeded by successful decisions.
9. At most one successful authorization exists for a payment ID.
10. Cancelled orders cannot be newly shipped.
11. Delivered orders must have been shipped.
12. A return ID and refund ID are each committed at most once.
13. Command reconciliation returns the original committed event batch.
14. Subscription event IDs and persisted event IDs remain globally ordered and equivalent for the observed interval.

## Concurrency Scenarios

### Inventory Contention

Two or more carts reserve the last units of one SKU/warehouse from the same observed boundary. One or more reservations commit until stock is exhausted. Remaining local decisions conflict, rehydrate, and return a domain rejection. Availability never becomes negative.

### Reservation Versus Checkout

A line reservation and checkout for the same cart start together. If reservation commits first, checkout retries and includes the line. If checkout commits first, reservation retries and rejects because the cart is closed.

### Coupon Limit

Many customers concurrently redeem a coupon near its global limit. Successful redemptions never exceed the limit. Redemptions of another coupon do not conflict.

### Duplicate Command

Several clients submit the same command ID concurrently. Exactly one batch is committed and every later execution reconciles to that batch.

### Remote Payment Staleness

A payment is prepared, then the order is cancelled or another payment event commits. Completion returns stale. An unrelated catalog change does not stale the capability.

## Operational Reads

The domain defines standard reusable queries:

| Read | Query |
|---|---|
| Inventory ledger | Inventory event types by `sku` and `warehouse` |
| Cart timeline | Cart and reservation event types by `cart` |
| Order timeline | Checkout, promotion, payment, shipment, return, and refund types by `order` |
| Customer activity | Cart, order, shipment, and return types by `customer` |
| Payment reconciliation | Payment event types by `payment` |
| Full audit | Event range snapshot through a captured head |

Inventory and order queries are exercised with both `EVENTUAL_INDEX` and `COMMITTED_SCAN`. Type-only and all-event queries deliberately exercise partition-scan fallback.

Subscriptions cover order placement, payment authorization, shipping, delivery, and refund events. The sample stores the last processed event ID externally and reconnects from that cursor; it does not imply durable server-side subscription state.

## Deterministic Fixture Profiles

| Profile | Products | Warehouses | Customers | Open carts | Target event volume | Purpose |
|---|---:|---:|---:|---:|---:|---|
| Tiny | 10 | 2 | 20 | 10 | approximately 100 | Smoke and sample walkthrough |
| Small | 100 | 4 | 1,000 | 250 | approximately 10,000 | Future local benchmark default |
| Medium | 1,000 | 8 | 10,000 | 2,000 | approximately 100,000 | Growth and read scaling |
| Custom | Configurable | Configurable | Configurable | Configurable | Configurable | Soak and profiling |

Fixture generation uses a recorded random seed. Workloads support uniform SKU selection, deterministic Zipfian selection, and a single hot SKU/warehouse. Future benchmark setup will record the seed, generated identifiers, starting head, and fixture parameters in its manifest.

## Project Boundary

```text
samples/Commerce/
  NativeDCB.Commerce/
    Commands/
    Events/
    Models/
    Fixtures/
    NativeCommerce.ndl
    CommerceDecisionDefinitions.cs
    CommerceNdl.cs
    CommerceSeeder.cs
  NativeDCB.Commerce.Sample/
    Program.cs
```

`NativeDCB.Commerce` is a reusable library. It owns contracts, generated schema factories, decision definitions, NDL source, deterministic fixture generation and population, reusable query shapes, semantic outcome expectations, correctness verification, and idempotent catalog seeding. It contains no benchmark timing or process-control code.

`NativeDCB.Commerce.Sample` is a console application with these modes:

| Mode | Behavior |
|---|---|
| `seed` | Create the database and idempotently register schemas and handlers without measured operations |
| `run` | Execute one complete purchase, fulfilment, return, and refund walkthrough |
| `contention` | Demonstrate inventory, checkout, coupon, and duplicate-command races with correctness output |
| `remote-payment` | Demonstrate prepare, successful completion, unrelated movement, and stale completion |
| `recovery` | Execute and replay a fixed command ID, then verify the original event batch through command reconciliation |

The client sample does not own the server process and therefore does not restart it. Server integration coverage reopens the same durable database root and verifies the Native Commerce catalog, events, and command reconciliation after restart.

## Implementation Coverage

- All event and command contracts compile through the SDK schema generator.
- The complete NDL document validates, and its 21 handlers publish through the idempotent seeder.
- Selected NDL and fluent SDK decisions produce equivalent plans.
- Seeding is idempotent and does not append domain events unless explicitly requested.
- The sample executes catalog, inventory, cart, checkout, promotion, payment, fulfilment, return, and refund flows.
- Concurrency examples verify final domain invariants rather than relying only on response counts.
- Native Commerce integration tests cover lifecycle ordering, local contention, duplicate-command reconciliation, successful and stale remote completion, deterministic fixtures, and durable restart.
- Indexed reads, committed reads, subscriptions, partition rollover, and process-level recovery remain covered by the general integration and end-to-end suites using the same server paths.
- The microbenchmark and system-benchmark projects reference the same fixture, query, outcome, and correctness library without copying domain definitions.
