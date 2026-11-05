# Integration Guides (Draft)

Short walk-throughs that test the [draft API](../api/openapi.yaml) against four kinds of payment method, before the contract is frozen.

Status: Draft · Last updated: 2026-11-05

Only cards are in version 1. The others are here to find out whether adding them later would force a new state machine ([D17](decisions.md#d17)). Bank debit and vouchers depend on `PROCESSING`, which is `Proposed`.

## Card

Authorize now, capture later:

1. `POST /v1/payments` with the amount, `method` and `capture_mode: manual`. Answer: 200, `AUTHORIZED`.
2. `POST /v1/payments/{id}/capture` when the goods ship. Answer: 200, `CAPTURED`.

If the card is declined, the answer is 402 with `failure_code`, and the payment returns to `AWAITING_METHOD`. Send another card to `POST /v1/payments/{id}/attempts` under a new key. There is still one payment for the order.

If the answer is 202, the outcome isn't known yet. Wait for the webhook or poll. Don't start again under a new key.

## Card with 3D Secure

1. `POST /v1/payments` as above. Answer: 200, `ACTION_REQUIRED`, with `latest_attempt.action`.
2. Send the customer to `action.url`.
3. Wait for the webhook, or poll `GET /v1/payments/{id}`, since the customer's return proves nothing by itself. `AUTHORIZED` means approved, and `AWAITING_METHOD` means try again.

A merchant that can't handle this sends `error_on_action: true`, and gets a failed attempt instead.

## Bank debit (not in version 1)

1. `POST /v1/payments` with `method.type: bank_debit` and `capture_mode: auto`. Answer: 200, `PROCESSING`.
2. Days later, a webhook reports `payment.captured` or `payment.attempt_failed`.

A failure after days returns the payment to `AWAITING_METHOD`, as with a card decline.

## Voucher (not in version 1)

1. `POST /v1/payments` with `method.type: voucher`. Answer: 200, `ACTION_REQUIRED`, where the action is to pay the voucher.
2. The customer pays in a shop within days.
3. A webhook reports `payment.captured`. If the voucher lapses, the payment returns to `AWAITING_METHOD`.

## What this showed

**No new state machine is needed.** All four fit the existing states plus `PROCESSING`. Four points follow for the contract:

| Finding | Effect |
|---|---|
| Bank debit and vouchers have no separate capture step | `capture_mode: manual` with such a method must answer 422 with a stable code. Add the rule when those methods are designed |
| A voucher's action window lasts days, not 15 minutes | The [action window](payment-lifecycle.md#timers) must be per method. `Action.expires_at` already allows this |
| `PROCESSING` isn't in the contract yet | Clients are told to tolerate new enum values, so adding it isn't a breaking change |
| Payments that skip `AUTHORIZED` still need correct ledger entries | Their fund flows need designing with the method. They can't be assumed from the card flow |
