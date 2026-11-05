# Event Catalog

The events Open Payments publishes from the outbox, and the shape every event shares.

Status: Draft · Last updated: 2026-11-05

When each event is sent, and which are `Proposed`, is in [Merchant events](payment-lifecycle.md#merchant-events). How events travel is in [Events and webhooks](architecture.md#events-and-webhooks). This doc defines their contents.

## Shape

Every event has the same fields, and is at most 32 KB.

| Field | Type | Meaning |
|---|---|---|
| `event_id` | UUID | Unique per event. Merchants and consumers deduplicate on it |
| `type` | string | A name from the table below. New types may be added |
| `schema_version` | integer | Version of this event's shape |
| `sequence` | integer | `payments.event_seq`: per payment, rising by one for each event ([D32](decisions.md#d32)) |
| `payment_id` | UUID | Also the topic key, so one payment's events stay ordered |
| `merchant_id` | UUID | The owning merchant |
| `state` | string | Payment state after the change |
| `occurred_at` | timestamp | Database time of the commit |
| `data` | object | Type-specific fields, below. IDs and amounts, never card data |

Delivery is at-least-once, so a consumer can see an event twice. A lower `sequence` than already applied is a duplicate only if that sequence was seen. An earlier sequence never seen is a gap, which is parked rather than dropped ([Stripe research](stripe-research.md#7-fix-event-ordering)).

## Events

| Type | `data` fields |
|---|---|
| `payment.authorized` | `attempt_id`, `amount_minor`, `currency` |
| `payment.attempt_failed` | `attempt_id`, `failure_code` |
| `payment.action_required` | `attempt_id`, `action_type`, `action_expires_at` |
| `payment.canceled` | `reason`: `merchant` or `window_ended` |
| `payment.captured` | `attempt_id`, `captured_minor`, `currency` |
| `payment.capture_failed` | `attempt_id`, `failure_code` |
| `payment.voided` | `attempt_id` |
| `payment.void_failed` | `attempt_id`, `failure_code` |
| `payment.expired` | `authorized_until` |
| `payment.refunded` | `refund_id`, `amount_minor`, `refunded_minor`, `currency` |
| `payment.refund_failed` | `refund_id`, `failure_code` |

`REVIEW` sends no event until it is resolved. A one-call sale sends `payment.captured` alone, so a merchant can't rely on `payment.authorized` coming first ([D29](decisions.md#d29)). A fraud block is a `payment.attempt_failed` with `fraud_blocked`.

## Thin or full events

Thin events carry only an ID and let the merchant fetch the current state. Full events carry a complete copy of the payment ([Stripe research](stripe-research.md#7-fix-event-ordering)).

**Decided by the owner on 2026-11-05: the middle shape above.** Events carry IDs, state and the amounts that changed, as [the architecture](architecture.md#events-and-webhooks) already says, and merchants fetch anything else. They avoid tying event shapes to a full API object, and most handlers need no extra status read, which matters at this volume. An event's fields are versioned by its own `schema_version`, separately from the API.

## Compatibility

Changes must be backward compatible: new fields and new types only. A schema registry enforces this, and contract tests run in CI (U5). Anything else needs a new `schema_version`, published alongside the old one.
