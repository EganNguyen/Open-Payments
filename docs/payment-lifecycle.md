# Payment Lifecycle

The states a payment can be in, the allowed moves between them, and the rules that keep those moves safe.

Status: Draft · Last updated: 2026-11-05

Rows marked `Proposed` need a decision before they are built. Everything else is `Committed`.

## Overview

A payment is not one write. It moves through several states, and some moves are driven by outside parties that can report late, twice or out of order.

Four ideas run through the lifecycle:

1. **Money moves in two steps.** Authorization reserves funds. Capture takes them.
2. **Unknown is a real state.** A PSP timeout is not a failure; the outcome is not known yet.
3. **A payment outlives its attempts.** A payment is what is owed for one order. An attempt is one try to collect it. A decline ends the attempt and returns the payment for another try ([D16](decisions.md#d16)).
4. **Every transition is a conditional, idempotent, ledgered write,** made through the [payment-transition module](architecture.md#components-and-boundaries).

```mermaid
stateDiagram-v2
  [*] --> AWAITING_METHOD: amount and currency known
  [*] --> PSP_PENDING: created with a method, attempt recorded
  AWAITING_METHOD --> PSP_PENDING: method given, attempt recorded
  AWAITING_METHOD --> CANCELED: merchant cancels or window ends
  PSP_PENDING --> AUTHORIZED: PSP approved
  PSP_PENDING --> AWAITING_METHOD: attempt declined
  PSP_PENDING --> ACTION_REQUIRED: customer must act
  PSP_PENDING --> UNKNOWN: timeout or crash
  PSP_PENDING --> REVIEW: amount or currency mismatch
  ACTION_REQUIRED --> PSP_PENDING: action finished
  ACTION_REQUIRED --> AUTHORIZED: PSP reports approved
  ACTION_REQUIRED --> AWAITING_METHOD: action failed
  ACTION_REQUIRED --> UNKNOWN: action window ends
  UNKNOWN --> AUTHORIZED: inquiry says approved
  UNKNOWN --> AWAITING_METHOD: inquiry says not approved
  UNKNOWN --> ACTION_REQUIRED: inquiry says customer must act
  UNKNOWN --> REVIEW: amount or currency mismatch
  AWAITING_METHOD --> REVIEW: late approval on a failed attempt
  AUTHORIZED --> CAPTURED: PSP confirms capture
  AUTHORIZED --> REVIEW: capture amount mismatch
  REVIEW --> AUTHORIZED: manual resolution
  REVIEW --> AWAITING_METHOD: manual resolution
  REVIEW --> CAPTURED: manual resolution
  CAPTURED --> PARTIALLY_REFUNDED: partial refund confirmed
  CAPTURED --> REFUNDED: full refund confirmed
  PARTIALLY_REFUNDED --> REFUNDED: remainder confirmed
  PSP_PENDING --> PROCESSING: accepted, result later (Proposed)
  PROCESSING --> CAPTURED: finalized (Proposed)
  PROCESSING --> AWAITING_METHOD: failed later (Proposed)
  AUTHORIZED --> VOIDED: void (Proposed)
  AUTHORIZED --> EXPIRED: window ends (Proposed)
  CAPTURED --> SETTLED: settlement matches (Proposed)
  SETTLED --> PAID_OUT: payout sent (Proposed)
  CAPTURED --> DISPUTED: chargeback opened (Proposed)
  SETTLED --> DISPUTED: chargeback opened (Proposed)
  DISPUTED --> CAPTURED: dispute won (Proposed)
  DISPUTED --> CHARGED_BACK: dispute lost (Proposed)
  CANCELED --> [*]
  VOIDED --> [*]
  EXPIRED --> [*]
  REFUNDED --> [*]
  CHARGED_BACK --> [*]
```

## States

| State | Meaning | Final? | Money position | Moved out by | Status |
|---|---|---|---|---|---|
| `AWAITING_METHOD` | Amount known; no attempt is live. New payment, or the last attempt failed | No | None | New attempt, cancel or window end | `Committed` |
| `PSP_PENDING` | Attempt recorded; PSP call in flight | No | None | PSP answer, timeout or crash recovery | `Committed` |
| `ACTION_REQUIRED` | Customer must act, such as 3D Secure or a redirect ([D17](decisions.md#d17)) | No | None | Action result or window end | `Committed` |
| `UNKNOWN` | PSP outcome not known | No | Maybe reserved | Reconciler | `Committed` |
| `REVIEW` | PSP answer disagrees on amount or currency, or a late approval arrived after a failed attempt | No | Under review | Manual resolution | `Committed` |
| `AUTHORIZED` | PSP approved; funds reserved | No | Reserved, not taken | Capture | `Committed` |
| `CAPTURED` | Funds taken from the customer | No | With the PSP, owed to the merchant | Refund | `Committed` |
| `PARTIALLY_REFUNDED` | Part of the captured amount returned | No | Partly returned | Further refund | `Committed` |
| `REFUNDED` | Full captured amount returned | Yes | Fully returned | n/a | `Committed` |
| `CANCELED` | Merchant canceled, or the payment window ended, with no successful attempt | Yes | None | n/a | `Committed` |
| `PROCESSING` | PSP accepted; the result comes later, as with bank debits. Unlike `UNKNOWN`, we know where it stands | No | Pending | PSP result | `Proposed` |
| `VOIDED` | Authorization cancelled before capture | Yes | Reservation released | n/a | `Proposed` |
| `EXPIRED` | Authorization window ended without capture | Yes | Released by the issuer | n/a | `Proposed` |
| `SETTLED` | Every settlement line for the payment has matched | No | In our settlement account | Payout | `Proposed` |
| `PAID_OUT` | Merchant has been paid | Yes, on the happy path | With the merchant | Open question | `Proposed` |
| `DISPUTED` | Customer's bank opened a chargeback | No | Held pending outcome | Dispute result | `Proposed` |
| `CHARGED_BACK` | Dispute lost; funds reversed | Yes | Returned to the customer | n/a | `Proposed` |

## Attempts

Each authorize or capture is one `payment_attempts` row with its own PSP key ([D5](decisions.md#d5)). Its states are `PENDING`, `ACTION_REQUIRED`, `UNKNOWN`, `SUCCEEDED`, `FAILED` and `REVIEW`. A decline is `FAILED` with a `failure_code`.

- **One live or successful attempt.** The unique index `payment_attempts_one_live_authorize` allows one authorize per payment in `PENDING`, `ACTION_REQUIRED`, `UNKNOWN` or `SUCCEEDED`, so two tries can't both charge. A new attempt while one is live gets 409 with `Retry-After`.
- **Every attempt is checked.** Each passes admission and the fraud check, because retries on one payment are also how card testing works.
- **Attempts are bounded.** A payment allows at most 10 authorize attempts as a starting value. After that, new attempts are rejected and the payment waits for its window to end.
- **Only the merchant or customer starts a new attempt.** The reconciler resolves attempts and never begins one.
- **Keys are per request.** A retry with the same `Idempotency-Key` returns the stored answer, including a decline. A new try needs a new key.

## Transitions

Each row is one allowed transition. Anything not listed is forbidden.

| From | To | Trigger | Source | Ledger effect | Status |
|---|---|---|---|---|---|
| `[*]` | `AWAITING_METHOD` | Create request with no method | Merchant | None | `Committed` |
| `[*]` | `PSP_PENDING` | Create request with a method; attempt recorded | Merchant | None | `Committed` |
| `AWAITING_METHOD` | `PSP_PENDING` | New attempt recorded | Merchant or customer | None | `Committed` |
| `AWAITING_METHOD` | `CANCELED` | Cancel request or payment window ends | Merchant or timer job | None | `Committed` |
| `PSP_PENDING` | `AUTHORIZED` | PSP approved | PSP response, callback or reconciler | Authorization hold | `Committed` |
| `PSP_PENDING` | `AWAITING_METHOD` | PSP declined; attempt `FAILED` | PSP response, callback or reconciler | None | `Committed` |
| `PSP_PENDING` | `ACTION_REQUIRED` | PSP requires customer action | PSP response or callback | None | `Committed` |
| `PSP_PENDING` | `UNKNOWN` | Timeout, or reconciler finds a stale attempt | Internal | None | `Committed` |
| `PSP_PENDING` or `UNKNOWN` | `REVIEW` | Amount or currency mismatch | PSP response, callback or reconciler | None for the disputed amount | `Committed` |
| `ACTION_REQUIRED` | `PSP_PENDING` | Customer finished; the same attempt continues | Customer return or callback | None | `Committed` |
| `ACTION_REQUIRED` | `AUTHORIZED` | PSP reports approved | Callback or reconciler | Authorization hold | `Committed` |
| `ACTION_REQUIRED` | `AWAITING_METHOD` | Action failed; attempt `FAILED` | Callback or reconciler | None | `Committed` |
| `ACTION_REQUIRED` | `UNKNOWN` | Action window ends | Timer job | None | `Committed` |
| `UNKNOWN` | `AUTHORIZED` | Inquiry says approved | Reconciler | Authorization hold | `Committed` |
| `UNKNOWN` | `AWAITING_METHOD` | Inquiry says not approved, or unknown attempt; attempt `FAILED` | Reconciler | None | `Committed` |
| `UNKNOWN` | `ACTION_REQUIRED` | Inquiry says the customer must act | Reconciler | None | `Committed` |
| `AWAITING_METHOD` | `REVIEW` | Late approval on a `FAILED` attempt | Callback or reconciler | None until resolved | `Committed` |
| `AUTHORIZED` | `CAPTURED` | PSP confirms capture | PSP response, callback or reconciler | Release the hold; debit `psp_receivable`, credit `merchant_payable` | `Committed` |
| `AUTHORIZED` | `REVIEW` | Capture amount mismatch | PSP response, callback or reconciler | None for the disputed amount | `Committed` |
| `REVIEW` | `AUTHORIZED`, `AWAITING_METHOD` or `CAPTURED` | Person compares the PSP record | Manual | Matching entries, or a reversing entry | `Committed` |
| `CAPTURED` | `PARTIALLY_REFUNDED` | PSP confirms a partial refund | PSP response, callback or reconciler | Debit `refund_reserved`, credit `psp_refund_payable` | `Committed` |
| `CAPTURED` | `REFUNDED` | PSP confirms a full refund | PSP response, callback or reconciler | Debit `refund_reserved`, credit `psp_refund_payable` | `Committed` |
| `PARTIALLY_REFUNDED` | `REFUNDED` | PSP confirms the remainder | PSP response, callback or reconciler | Debit `refund_reserved`, credit `psp_refund_payable` | `Committed` |
| `PSP_PENDING` | `PROCESSING` | PSP accepted; result comes later | PSP response or callback | None | `Proposed` |
| `PROCESSING` | `CAPTURED` | Payment finalized | Callback or reconciler | Debit `psp_receivable`, credit `merchant_payable` | `Proposed` |
| `PROCESSING` | `AWAITING_METHOD` | Payment failed later; attempt `FAILED` | Callback or reconciler | None | `Proposed` |
| `AUTHORIZED` | `VOIDED` | Void request | Merchant | Release hold | `Proposed` |
| `AUTHORIZED` | `EXPIRED` | Window ends | Timer job | Release hold | `Proposed` |
| `CAPTURED` | `SETTLED` | All settlement lines matched | Reconciler | None; lines post their own entries | `Proposed` |
| `SETTLED` | `PAID_OUT` | Payout sent | Payout service | Pay merchant from settled | `Proposed` |
| `CAPTURED` or `SETTLED` | `DISPUTED` | Chargeback notice | PSP | Hold disputed amount | `Proposed` |
| `DISPUTED` | `CAPTURED` | Dispute won | PSP | Release hold | `Proposed` |
| `DISPUTED` | `CHARGED_BACK` | Dispute lost | PSP | Reverse entries, add dispute fee | `Proposed` |

## Transition rules

### Every transition is a conditional update

```sql
update payments
   set state = 'CAPTURED', captured_minor = $amount,
       version = version + 1, updated_at = now()
 where id = $payment_id and merchant_id = $merchant_id
   and state = 'AUTHORIZED' and $amount = amount_minor; -- full capture; partial capture is Proposed
```

Zero rows updated means someone else already moved the payment. The caller re-reads and decides; it never overwrites.

### State, ledger and outbox commit together

In one short transaction: conditionally update the state, insert the ledger entries, insert the outbox event. The PSP call happens before this transaction, never inside it.

An "authorization hold" is a logical ledger entry, `auth_hold` against `auth_hold_contra`, because no money has moved. Customer funds aren't an account we hold. The accounts are in [Fund flows](architecture.md#ledger) ([D21](decisions.md#d21)).

### Idempotency

- Every create, new-attempt, cancel, capture and refund request carries an `Idempotency-Key`, as would `Proposed` void requests.
- A retry with the same key returns the stored result and does not repeat the transition.
- The same key with a different body is rejected with 422.
- The same key while the first request is running returns 409 with `Retry-After`.
- Refunds use their own key on the `refunds` table.
- Keys are kept for at least 24 hours after their operation resolves, and never expire while it is unresolved ([D19](decisions.md#d19)). The module sets `resolved_at` in the same transaction as the attempt's final state.

### Late, duplicate and out-of-order events

Each PSP event is recorded once in `psp_events`. A conditional update that finds the payment already in a later or final state does nothing. The event is logged, and an alert is raised if it contradicts stored state, such as "declined" on a `CAPTURED` payment.

**A late approval on a `FAILED` attempt is the dangerous case,** because the customer may now be charged by two attempts. The attempt moves to `REVIEW` and an alert fires, with no ledger entry until a person resolves it. If the payment is in `AWAITING_METHOD`, it also moves to `REVIEW`, which blocks new attempts. Otherwise its state doesn't change, and the person reverses the extra charge at the PSP.

### Amounts and currency

Every PSP answer is checked. On an authorize or capture mismatch the payment moves to `REVIEW`, and no ledger entry is written for the disputed amount ([D12](decisions.md#d12)). A refund mismatch moves the [refund](#refund-rules) to `REVIEW` instead.

### Final states

`CANCELED`, `REFUNDED`, and the `Proposed` `VOIDED`, `EXPIRED` and `CHARGED_BACK` accept no further transitions. A canceled payment can't be retried, so another try means a new payment.

## Refund rules

Total refunded and pending must never exceed the captured amount, and state changes only after the PSP confirms ([D6](decisions.md#d6)). Refund states are `PENDING`, `UNKNOWN`, `SUCCEEDED`, `FAILED` and `REVIEW`.

**1. Reserve.** In one transaction, in this [lock order](deadlock-handling.md#prevention): take the per-account [advisory lock](architecture.md#ledger) and check the available balance, reserve the amount as below, insert the `refunds` row as `PENDING`, and insert a ledger reservation entry. If the balance is too low or the update returns 0 rows, roll back and reject.

```sql
update payments
   set refund_pending_minor = refund_pending_minor + $amount,
       version = version + 1, updated_at = now()
 where id = $payment_id and merchant_id = $merchant_id
   and state in ('CAPTURED','PARTIALLY_REFUNDED')
   and refunded_minor + refund_pending_minor + $amount <= captured_minor
returning refund_pending_minor;
```

**2. Call the PSP** with `refund_id` as the idempotency key, outside any transaction.

**3. Confirm.** On approval, in one transaction, run the update below, then conditionally move the refund from `PENDING` or `UNKNOWN` to `SUCCEEDED`. If that moves no row, another worker got there first, so roll back. Otherwise post the ledger entries that reverse the reservation and record the refund, then the outbox event.

```sql
update payments
   set refunded_minor = refunded_minor + $amount,
       refund_pending_minor = refund_pending_minor - $amount,
       state = case when refunded_minor + $amount = captured_minor
                    then 'REFUNDED' else 'PARTIALLY_REFUNDED' end,
       version = version + 1, updated_at = now()
 where id = $payment_id and merchant_id = $merchant_id
   and refund_pending_minor >= $amount;
```

**Or release.** On a PSP decline, subtract the amount from `refund_pending_minor`, conditionally move the refund to `FAILED`, post a reversing entry for the reservation, and insert the outbox event. Roll back if the refund moved no row.

**On a timeout,** the refund becomes `UNKNOWN`, the amount stays reserved, and the reconciler confirms or releases it. If the PSP reports no such refund, for example after a crash before the call, the reconciler releases it.

**On an amount or currency mismatch,** the refund moves to `REVIEW` and the payment state does not change. The amount stays reserved until a person confirms or releases it.

## Customer action

A PSP can answer that the customer must act, for example with 3D Secure ([D17](decisions.md#d17)).

1. The attempt and payment move to `ACTION_REQUIRED`, and the API returns what the customer must do. The details are stored on the attempt, never card data.
2. When the customer finishes, the same attempt continues with the same PSP key. A callback may report the result directly.
3. If the action fails, the attempt is `FAILED` and the payment returns to `AWAITING_METHOD`.
4. If the action window ends, the state becomes `UNKNOWN` and the reconciler inquires. An abandoned action is never assumed to have failed, because the customer may have completed it just as the window closed.

A merchant that can't handle customer action can set a flag on the request, and the API then answers with a failed attempt instead of `ACTION_REQUIRED`.

## Settlement lines

The reconciler ingests PSP settlement files line by line ([D18](decisions.md#d18)). Lines track settlement, and **a line never changes the payment state**, because refunds can still be running on a settled payment.

| Line state | Meaning | Effect |
|---|---|---|
| `RECEIVED` | Parsed and stored; ingesting the same file again changes nothing | None yet |
| `MATCHED` | One operation found by PSP reference, with the same amount and currency | Ledger entries clearing the PSP receivable or payable, through the payment-transition module |
| `UNMATCHED` | No operation has this reference | Discrepancy record that pages; no ledger entry |
| `MISMATCH` | Operation found, but the amount or currency differs | Discrepancy record that pages; no ledger entry |

An operation with no settlement line past its expected date also raises a discrepancy. Fee and adjustment lines are stored, and their ledger treatment is `Open`.

## The Unknown state

A PSP timeout does not tell us whether the customer was charged.

- The API returns `202 Accepted` with the payment ID. The merchant learns the result by webhook or polling.
- The reconciler asks the PSP for the attempt's status and applies the answer.
- Never retry a charge without the same PSP idempotency key, or the customer can be charged twice.
- If the PSP reports an unknown attempt, an authorize attempt is marked `FAILED` and the payment returns to `AWAITING_METHOD`, while a capture attempt is marked `FAILED` so a new capture can be requested.
- While an attempt is `UNKNOWN`, no new attempt can start, because the one-live-attempt index blocks it.
- Capture attempts and refunds stay unresolved on their own rows, and the payment state does not change until they resolve.
- A payment, capture attempt or refund unresolved beyond a time limit raises an alert. A stuck refund also blocks its reserved amount.

## Capture modes

| Mode | Behavior | Use when | Status |
|---|---|---|---|
| Authorize, then capture | Funds are reserved, and the merchant captures later | Physical goods, hotels, rentals | `Committed` |
| Auto-capture | Capture is triggered right after authorization | Digital goods, instant services | `Committed` |
| Partial capture | Capture less than authorized; the remainder is released | Partial shipment | `Proposed` |

The capture amount must not exceed the authorized amount. Capture and refund requests at the same moment are serialized by conditional updates: one wins and the other re-reads.

## Timers

Timer jobs use the same conditional update, so a job and a manual capture cannot both win.

| Timer | Applies to | Action | Status |
|---|---|---|---|
| Attempt timeout | `PSP_PENDING` | Move to `UNKNOWN`, then inquire. These payments are found through the partial index `payments_unresolved` | `Committed` |
| Action window (starting value 15 minutes) | `ACTION_REQUIRED` | Move to `UNKNOWN`, then inquire. Found through `payment_attempts_action_window` | `Committed` |
| Payment window (starting value 24 hours) | `AWAITING_METHOD` | Move to `CANCELED`. Found through `payments_awaiting` | `Committed` |
| Idempotency key expiry | Keys resolved at least 24 hours ago | Delete in batches; never a key with no `resolved_at` | `Committed` |
| Unknown age limit | `UNKNOWN` | Alert and escalate to manual review | `Committed` |
| Unresolved operation | Capture attempts and refunds, through `payment_attempts_unresolved` and `refunds_unresolved` | Inquire; alert and escalate past the age limit | `Committed` |
| Authorization window (for example 7 days, depending on card scheme) | `AUTHORIZED` | Move to `EXPIRED` and release the hold | `Proposed` |
| Dispute response deadline | `DISPUTED` | Alert so evidence is submitted in time | `Proposed` |

## Worked examples

**Happy path with a partial refund:**

| Step | Action | State after | Ledger |
|---|---|---|---|
| 1 | Merchant creates a payment of 100.00; API records the attempt and calls the PSP | `PSP_PENDING` | none |
| 2 | PSP approves | `AUTHORIZED` | hold 100.00 |
| 3 | Merchant captures 100.00; PSP confirms | `CAPTURED` | hold released; `psp_receivable` to `merchant_payable`, 100.00 |
| 4 | Merchant refunds 30.00; reserved while the PSP is called | `CAPTURED` | reservation entry 30.00, `refund_pending_minor = 30.00` |
| 5 | PSP confirms the refund | `PARTIALLY_REFUNDED` | reservation reversed, refund recorded, `refunded_minor = 30.00`, `refund_pending_minor = 0` |
| 6 | Merchant refunds 70.00; reserved, then the PSP confirms | `REFUNDED` | same steps, `refunded_minor = 100.00` |

**Declined, then retried on the same payment:**

| Step | Event | Payment state | Attempt |
|---|---|---|---|
| 1 | Merchant creates a payment of 100.00 with a card | `PSP_PENDING` | A `PENDING` |
| 2 | PSP declines; the API returns the decline and `payment.attempt_failed` is sent | `AWAITING_METHOD` | A `FAILED` |
| 3 | Merchant retries with attempt A's key | `AWAITING_METHOD` | Stored decline returned; nothing new |
| 4 | Customer gives another card, under a new key | `PSP_PENDING` | B `PENDING` |
| 5 | PSP approves | `AUTHORIZED` | B `SUCCEEDED`; hold 100.00 |

**3D Secure:**

| Step | Event | Payment state | Attempt |
|---|---|---|---|
| 1 | PSP answers that authentication is required; the API returns the action | `ACTION_REQUIRED` | `ACTION_REQUIRED` |
| 2 | Customer finishes; the attempt continues under the same PSP key | `PSP_PENDING` | `PENDING` |
| 3 | PSP approves | `AUTHORIZED` | `SUCCEEDED` |

**Timeout, then recovery:**

| Step | Event | State after |
|---|---|---|
| 1 | API calls the PSP; no answer within the deadline | `UNKNOWN` |
| 2 | API returns `202 Accepted` with the payment ID | `UNKNOWN` |
| 3 | Reconciler asks the PSP about the attempt; PSP says approved | `AUTHORIZED`, webhook sent |
| 4 | A late "declined" callback arrives | No change; logged and alerted |

## Merchant events

An outbox event is written in the same transaction as every state change, and the outbox publisher moves it toward delivery. Delivery is at-least-once, so each event has a unique ID and the merchant de-duplicates. `REVIEW` sends no event until it is resolved.

| Event | Sent on | Status |
|---|---|---|
| `payment.authorized` | `AUTHORIZED` | `Committed` |
| `payment.attempt_failed` | An authorize attempt ends `FAILED`; the payment can be retried | `Committed` |
| `payment.action_required` | `ACTION_REQUIRED` | `Committed` |
| `payment.canceled` | `CANCELED` | `Committed` |
| `payment.captured` | `CAPTURED` | `Committed` |
| `payment.refunded` | `PARTIALLY_REFUNDED`, `REFUNDED` | `Committed` |
| `payment.capture_failed` | A capture attempt ends `FAILED` | `Committed` |
| `payment.refund_failed` | A refund ends `FAILED` | `Committed` |
| `payment.voided`, `payment.expired` | `VOIDED`, `EXPIRED` | `Proposed` |
| `payment.disputed` | `DISPUTED` | `Proposed` |

## Proposed rules

- **Void and expiry:** capture after `VOIDED` or `EXPIRED` is rejected ([E8](failure-scenarios.md#edge-cases)).
- **Dispute after a full refund:** reject or defend, showing the refunded amount in evidence.
- **Payouts:** query the PSP before retrying a payout.

Other unusual cases are [edge cases](failure-scenarios.md#edge-cases) E6, E7, E8 and E10, and [failures](failure-scenarios.md#failure-catalogue) FS-07, FS-09 and FS-32.

## Open questions

1. Which `Proposed` states to build first, and which wait for the settlement and payout phase.
2. Authorization window per card scheme and per PSP.
3. Whether auto-capture is a flag on the payment or a merchant setting.
4. How long a payment may stay `UNKNOWN` before manual review.
5. Whether `PAID_OUT` and `CHARGED_BACK` belong in the payment record or a separate settlement record.
6. Which markets need 3D Secure in production, and each PSP's action window.
7. How to treat fee and adjustment settlement lines, and the expected settlement date per PSP.
8. Whether refunds are allowed after `SETTLED` or `PAID_OUT`. A real system needs this, but no transition exists yet.
9. Whether disputes can open from `PARTIALLY_REFUNDED`, `PAID_OUT` or `REFUNDED`.
