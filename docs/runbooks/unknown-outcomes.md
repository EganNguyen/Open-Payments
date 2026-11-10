# Unknown Outcomes

What to do when payments, attempts or refunds stay unresolved. Stage 1 records uncertainty safely but can't resolve it: the reconciler and callback receiver arrive in Stage 2 ([D46](../decisions.md#d46)).

Status: Stage 1 procedure · Last updated: 2026-11-10

## What it means

An operation is unresolved when its outcome at the PSP isn't established: a payment in `PSP_PENDING` or `UNKNOWN`, or an attempt or refund in `PENDING` or `UNKNOWN`. It happens when the PSP call timed out or failed after the request may have been sent, or when the API stopped before recording the answer ([D27](../decisions.md#d27), [D28](../decisions.md#d28)). **The customer may have been charged.**

The [recovery flows](../failure-scenarios.md#recovery-flows) show how these cases end: FS-09, FS-35, FS-43, FS-45 and FS-46.

## How you see it

- **Alert** `UnresolvedOperationAging`: something has been unresolved for over 60 seconds, CK5's threshold.
- **Metrics** `op_unresolved_count`, `op_unresolved_oldest_age_seconds` and `op_unresolved_amount_minor`, by kind, state, currency and PSP ([Metrics](../metrics.md)).
- **Console:** Payments, filtered by state "Outcome unknown" or "Sent to PSP".
- **Check:** CK5 fails while any remain.

## What never to do

- **Never mark it failed, and never tell the merchant it failed.** A timeout isn't a decline.
- **Never retry with a new idempotency key.** That creates a second payment and can charge twice. A retry with the same key is safe: it renders the current state, with no second PSP call.
- **Never edit the database by hand.** State, ledger and outbox change only through the transition module.

## What to do in Stage 1

1. **Find the reference.** Each attempt's ID is its idempotency key at the PSP, written before the call. Refunds use the refund's ID.
2. **Ask the PSP.** Look the attempt up at the PSP by that key: the PSP dashboard, or for the mock, `GET /inquire/{attempt_id}` with the account header.
3. **Tell the merchant what's known.** "Being confirmed" for an unknown outcome, with the PSP's answer if you have one. Point them at the same-key retry, which shows the current state.
4. **Record the evidence** against the payment in the support system: PSP answer, time and who checked. Stage 2's reconciler and the console's REVIEW resolution (C5, C12) apply the outcome with its ledger entries. Until then the payment stays unresolved in Open Payments, even when the PSP's answer is known.
5. **Escalate** if the oldest is over an hour old or the amount keeps growing. It usually means the PSP or a database standby is slow.

## On a benchmark stack

Load runs on the laptop leave unresolved rows when PSP calls time out, for example in the step 6 stall of 2026-11-09. They hold no real money. Reset the stack with [the benchmark reset](../../benchmarks/README.md#full-benchmark-reset) before a run that must pass CK5.
