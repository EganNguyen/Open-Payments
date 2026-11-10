# Frequently Asked Questions

Short answers to the questions a new reader, a reviewer or a hiring panel asks
about Open Payments. Each answer explains the concept first, then says what this
project actually does about it.

Status: living document · Last updated: 2026-11-10

The file is both a FAQ and the project's engineering knowledge map: the payment
concepts, the WAL and durability model, the evidence rules, and the study
priorities. Answers describe the design and the Stage 1 build; where the line is
crossed, the answer says so.

**Do not blur three states of a capability:** designed in the docs, implemented
in the code, and proven by a recorded run. A designed capability is not
implemented, and an implemented one is not verified. Section 14 says what exists
where; section 12 says what evidence exists.

**Each topic has one canonical section.** Where a concept reappears — `UNKNOWN`
in section 5, idempotency in section 4, durability in section 9 — the repeat is a
short pointer, not a second explanation. Change the canonical section first.

**If you read only one answer,** read [Why save the attempt before calling the
PSP?](#why-save-the-attempt-before-calling-the-psp). It is the idea the rest of
the system is built around.

## Contents

1. About the project
2. Payment concepts, in this project's terms
3. The payment flow
4. Idempotency and duplicate charges
5. `UNKNOWN`, callbacks and reconciliation
6. Money and the ledger
7. Failure and recovery
8. Database and concurrency
9. WAL, durability and crash recovery
10. Performance and overload
11. Security, tenancy and compliance
12. Verification and evidence
13. Status, scope and why things are the way they are
14. Where this lives in the code
15. How should I prioritise learning this?
16. Answers to six review questions
17. Glossary

---

## 1. About the project

### What is Open Payments?

A card payment processing API and platform, written in C# on .NET 10. A business
(a merchant) uses it to charge a customer's card through **its own** PSP
account, refund the charge, and see what happened.

The goal is not "accept a payment". The goal is: **5,000 completed payments per
second without ever double-charging anyone or losing a record of money.**

### Who holds the money?

Nobody at Open Payments. The merchant has its own account with a payment service
provider (PSP) — Stripe, Adyen, a bank. We call the PSP on the merchant's behalf,
using the merchant's credentials. The money moves inside the merchant's PSP
account and later to the merchant's bank. We only record what happened (D35).

This one decision removes a large amount of scope: no customer wallets, no
stored value, no payouts, no negative balances in version 1.

### What is actually built, and what is only on paper?

**Built (Stage 1, Tasks 1–18):**

- Payments core: create, attempt, capture, void, cancel, refund, status.
- Payments API with API-key auth and idempotency.
- A mock PSP that models latency and errors.
- The ledger, the outbox rows (written with the payment), and the
  payment-transition module.
- Correctness checks CK1–CK10, as separate code. CK11 is defined for
  client-view reconciliation.
- A load harness (k6) and a staff console with its first screens.
- Crash tests K1–K3 against the real API, and alerts for unresolved outcomes.

**Designed, not built:**

- The reconciler that resolves `UNKNOWN` outcomes automatically.
- The outbox publisher that ships committed events to the broker.
- PSP callback receiver in production form, real PSP adapters, settlement file
  ingestion, 3DS/`ACTION_REQUIRED` end to end.
- Payouts, fees, disputes/chargebacks, partial capture.

**Not measured:**

- Any capacity number. All benchmark results are laptop previews ([BASELINE](../benchmarks/BASELINE.md)),
  because there is no Linux host yet (D53).

### Why these technologies?

| Choice | Reason | Decision |
|---|---|---|
| .NET 10 | Predictable latency, good async I/O, strong typing for money code | D11 |
| PostgreSQL | The constraints *are* the correctness guarantee: conditional updates, unique partial indexes, deferred triggers, synchronous replication | Architecture |
| Kafka/Redpanda | Durable, ordered, replayable event delivery for the outbox | Architecture |
| One database per cell | One transaction can cover state, ledger and outbox | D15 |

The database choice is not "we like Postgres". It is that PostgreSQL can enforce
"debits equal credits" and "one live attempt per payment" *at commit time*, so a
bug in the application cannot commit bad money state.

### What does "completed payments per second" mean?

The project defines it as **an attempt whose request got a final committed
answer**: authorized, captured under auto-capture, or declined. `UNKNOWN` and
`ACTION_REQUIRED` do not count until resolved.

That definition counts attempts, not business payments. A payment declined twice
and then approved contributes three. Read strictly, the number is **completed
attempts per second**, and the 5,000 target inherits that definition (D3).

Two metrics should therefore stay separate:

| Metric | Counts | Status |
|---|---|---|
| `completed_attempts_per_second` | each PSP operation that reached a final committed answer | what the target and the harness measure today |
| `completed_payments_per_second` | distinct business payments reaching a terminal outcome | not measured |

Without the second metric, a client that retries more can raise the first without
processing more business. That is also why the target converts to req/s only
through the workload mix (D47).

It is not requests per second. One payment can be one call (sale) or two
(authorize then capture), so 5,000 attempts/s is roughly 10,000 req/s at the
benchmark mix.

### Why build one cell first?

A **cell** is a complete independent stack — API, database, broker, workers —
serving a subset of merchants. Cells limit blast radius: one bad merchant or one
broken database affects one cell, not everyone.

Building two cells before one works doubles the operational surface and proves
nothing. One cell first (D14), split only if a measured gate says so.

---

## 2. Payment concepts, in this project's terms

### Authorize, capture, sale, void, refund, chargeback — what is the difference?

| Operation | What it does | Money position |
|---|---|---|
| **Authorize** | Asks the issuer to reserve funds on the card | Reserved, not taken |
| **Capture** | Takes the reserved funds | Taken, owed to the merchant |
| **Sale** | Authorize and capture in one PSP call | Taken, immediately |
| **Void** | Cancels an authorization before capture | Reservation released |
| **Refund** | Returns captured money to the customer | Returned |
| **Chargeback** | The customer's bank reverses a captured payment after a dispute | Taken back |

Each one is a separate PSP operation with its own idempotency key. A payment can
have many authorize attempts (the customer retries), but only one can be live or
successful.

### What is an "attempt" and why is it a separate row?

An attempt is **one try to move money**: one authorize, sale, capture, void or
refund, with its own `attempt_id`, its own PSP idempotency key and its own
outcome.

A payment is the business object ("charge order #1234, $24"). An attempt is one
PSP interaction. They are separate because:

- A payment can be declined twice and then approved. The history matters.
- A late approval has to be voided *specifically*, so you must know which
  attempt it was.
- Unique indexes can then say "one live authorize per payment" and "one live
  capture or void per payment" — the database makes a duplicate **local** attempt
  impossible. The external half is the PSP's idempotency contract, which is a
  weaker and time-limited guarantee (section 4).

### What is a payment window?

Every payment has `expires_at`. A payment that is never completed (customer
never supplies a method) is canceled when the window ends, so unfinished rows do
not accumulate forever. Authorizations also have their own expiry
(`authorized_until`): the issuer releases the hold after a few days if nobody
captures.

### What is 3D Secure / `ACTION_REQUIRED`?

Some cards require the customer to prove it is them — a redirect to their bank,
an app approval, a one-time code. The PSP answer is neither approved nor
declined, it is "the customer must act".

The **domain** supports the `ACTION_REQUIRED` state, a customer-action window,
and the intended same-attempt continuation after the customer returns (D17).
The **end-to-end flow** — customer redirect, return/callback handling, and a real
PSP integration — is not implemented. A full SCA product is out of scope.

### What is a settlement line?

The PSP does not send money the moment you capture. It batches: once a day (or
more often) it pays the merchant the total, minus fees, and publishes a **file**
listing every capture, refund, fee and adjustment in that batch.

Each row of that file is a settlement line. Matching our records against those
lines is how we prove the PSP agrees with our ledger. An unmatched line is a
real problem and pages a human.

### What is a PSP account, and why is it per merchant?

A `psp_accounts` row is one merchant's connection to one PSP: provider name,
mode (live/test), a reference to credentials held in an external secret store,
and a rate limit.

It is per merchant because we act for the merchant, not for ourselves: their
credentials, their contract, their rate limit, their money. A revoked credential
must fail one merchant fast, not everyone (see circuit breakers).

### What is test mode?

A merchant can call the API with a test key and get simulated payments, separate
from live data (D20). Test keys must never touch live money or live merchant
data. The isolation mechanism is still an open decision; the requirement is not.

---

## 3. The payment flow

This is the full designed flow. Section 14 marks which parts are stubs in the
current build.

### Walk me through one $24 card payment.

1. **Admit.** Authenticate the API key. Rate-limit and take an in-flight permit.
   Over capacity → 429/503 with `Retry-After`, immediately.
2. **Read the idempotency key.** A finished request with this key returns its
   stored answer right now, with no fraud check. A running one returns 409.
3. **Fraud check**, within a 50 ms budget. A block writes a `FAILED` attempt
   with `fraud_blocked`, makes no PSP call, and returns 402.
4. **Transaction A** (no network calls): insert the idempotency record, move the
   payment to `PSP_PENDING`, insert the attempt with its own `attempt_id`.
   Commit. If this fails, do not call the PSP.
5. **Call the PSP** with `attempt_id` as the idempotency key and reference, with
   a deadline, breaker and rate limiter. Client disconnects no longer cancel it.
6. **Transaction B**: move the payment to its new state, update the attempt,
   insert ledger entries and the outbox event. Commit.
7. **Answer**: success, decline (try again), action required, or 202 unknown.

Later, asynchronously: the outbox publisher tells the merchant; the reconciler
resolves anything unresolved; settlement lines match the ledger.

### Why save the attempt before calling the PSP?

Because the dangerous window is *after* the PSP charged and *before* we recorded
it. You cannot make that atomic: the PSP is outside your database.

If the attempt row already exists, a crash leaves something findable. The
reconciler can ask the PSP "what happened to attempt X?" and record the true
answer. If the row did not exist, the charge exists only at the PSP, and nobody
knows which of thousands of payments it belongs to.

This is the rule behind crash test K1: kill the real API after the charge, before
the commit, and prove the attempt is still findable.

### What is "transaction A" and why is it separate from "transaction B"?

**A** writes intent: payment state + attempt, committed before any external call.
**B** writes the result: final state + ledger + outbox, committed after the PSP
answers.

They are separate because a PSP call must never happen inside a database
transaction. Holding a transaction open across a network call holds locks, pins a
connection, and turns a slow PSP into a database outage. Two short transactions
are also what make crash recovery well-defined: the gap between them is exactly
the `UNKNOWN` window.

### What does the API return for each PSP outcome?

| PSP result | Payment | Attempt | HTTP answer |
|---|---|---|---|
| Approved | `AUTHORIZED` (or `CAPTURED` for a sale) | `SUCCEEDED` | Success |
| Declined | `AWAITING_METHOD` | `FAILED` + `failure_code` | The decline, retryable |
| Requires action | `ACTION_REQUIRED` | `ACTION_REQUIRED` | What the customer must do |
| Timeout | `UNKNOWN` | `UNKNOWN` | **202 Accepted** |

The important row is the last one. A timeout is not an error response, because
we do not know that it failed. Telling the client "failed" would invite a retry
that could charge twice.

### Why does a decline return the payment to `AWAITING_METHOD`?

Because a decline ends *that attempt*, not the *payment*. The customer can try a
different card. The payment returns to a state where a new attempt is allowed
(D16). The attempt stays `FAILED` with a reason code, so the history is intact.

The attempt cap (10 by default) stops this from becoming an unlimited card-testing
oracle.

### How does capture work?

Insert a capture attempt with its own idempotency key, call the PSP, and move the
payment to `CAPTURED` only after the PSP confirms. A unique partial index allows
one live or successful capture or void per payment, so a second capture request
with a different key never reaches the PSP: it gets the current status, or 409
while the first is running.

On a timeout the payment stays `AUTHORIZED`, the attempt stays `UNKNOWN`, the API
answers 202, and the reconciler resolves it.

### How does a refund work?

Three steps: **reserve, call, confirm or release.**

1. **Reserve** in one transaction: conditionally increment `refund_pending_minor`
   on the payment (which checks the limit), insert the refund as `PENDING`, insert
   a ledger reservation. If the update changes zero rows, roll back and reject.
2. **Call the PSP** with the refund's own idempotency key.
3. **Confirm** (state → `SUCCEEDED`, ledger moves the reservation to
   `psp_refund_payable`) or **release** (state → `FAILED`, reservation reversed).

Reserving before calling is what stops ten concurrent refunds from each passing a
"is there room?" check and collectively exceeding the captured amount.

### What endpoints exist today?

| Method | Path | Purpose |
|---|---|---|
| POST | `/v1/payments` | Create a payment, optionally with a method |
| POST | `/v1/payments/{id}/attempts` | Try again with a new method |
| GET | `/v1/payments/{id}` | Status |
| POST | `/v1/payments/{id}/capture` | Capture |
| POST | `/v1/payments/{id}/void` | Void an authorization |
| POST | `/v1/payments/{id}/cancel` | Cancel a payment |
| POST | `/v1/payments/{id}/refunds` | Refund |
| GET | `/v1/refunds/{id}` | Refund status |
| GET | `/health`, `/metrics` | Liveness and Prometheus metrics |

Every mutating route requires an `Idempotency-Key` header. The contract is draft
until C39 in [the API spec](../api/openapi.yaml).

---

## 4. Idempotency and duplicate charges

### Can a payment system be exactly-once?

No — not end to end. Once a network and a provider you do not control are
involved, the only honest model is **at-least-once delivery plus effects that are
idempotent**: repeating the effect is a no-op, so the observable result is one
charge.

Three claims that sound like exactly-once but are not:

- **"Kafka gives exactly-once."** It gives exactly-once inside Kafka's
  read-process-write loop, with transactional producers and consumers. The moment
  you call a PSP or a merchant's webhook endpoint, that guarantee ends.
- **"Two-phase commit makes it atomic."** The PSP is not a participant in your
  transaction, so there is no second phase to run there.
- **"We dedupe in the application."** A check-then-act in application code is a
  race. Only a database constraint or an atomic conditional update is safe.

Here, the system prevents duplicate **effects** under its database invariants and
the PSP's idempotency contract. Three layers do the work: the unique partial
index for one live attempt, the conditional state update, and a PSP key per
operation. Where those are insufficient — a late approval, or a provider that
forgot the key — the system detects and contains the case instead of pretending
it cannot happen.

No local database constraint can make an external PSP charge mathematically
impossible. The guarantee is only as strong as the provider contract plus the
detection behind it. CK2 checks the provider's record afterwards; it can reveal a
duplicate, not prevent one.

### What is idempotency, in one sentence?

Repeating the same request has the same effect as doing it once — and returns the
same answer instead of doing the work again.

### What are the cases, and what does the API answer?

| Case | Answer |
|---|---|
| Same key, same request, finished | The stored response, replayed. No PSP call, no new attempt |
| Same key, same request, still running | 409 with `Retry-After` |
| Same key, **different** method/route/body | 422 `idempotency_key_reused` |
| Same key, earlier request refused before doing work | Allowed to run now, because nothing was stored |
| Missing key on a mutating route | 400 |

The third case matters: silently replaying an old answer for a different request
would hide a client bug and could hide money.

The status codes are a design choice, not an HTTP requirement. `409` for
in-flight and `422` for a key reused with a different body are this project's
contract; another provider might use `425` or `200`. What matters is that every
case is distinguishable, documented, and cannot silently replay the wrong answer.

### How long are keys kept, and when can one be reused?

| Question | This project's answer |
|---|---|
| When does expiry start? | At `resolved_at`, set in the same transaction as the operation's final state. Never while unresolved (D19) |
| How long after that? | At least 24 hours |
| Can an expired key be used again? | Yes, but as a **new request**, not a replay. The old row is history; the client must not expect the old answer |
| Is the request hash kept? | Yes, for the whole window. Every reuse compares method, route and body; a mismatch is 422 |
| What if the client retries after our window? | It is indistinguishable from a new request, so it gets a new attempt and a new PSP key. That is a new charge, and it must be a deliberate client decision |

The trap is the provider's clock. **PSP retention is measured from the first
provider request, not from when we resolve the operation.** An attempt that stays
`UNKNOWN` for hours can consume the provider's window while our 24 hours have not
even started. Re-sending that operation with the same key after the provider has
forgotten it is how a "retry" becomes a second charge.

So the rule is not "24 hours is safe". The rule is:

- Replay with the same key only well inside the provider's documented retention.
  Each adapter declares its recovery mode and safe window; the mock declares two
  hours, Stripe documents at least 24 hours, Adyen seven days or more.
- Past that window, recover by authoritative lookup or a technical cancel — never
  by blind replay.
- If no mode can establish the answer, the attempt stays `UNKNOWN` and escalates.

24 hours is a **policy chosen to match a documented provider minimum**, not a
universal guarantee. A different PSP may force a different number.

### What happens if two requests race with the same key?

The `(merchant_id, key)` primary key makes one of them win. The loser gets 409
with `Retry-After`, or reads the stored answer if the winner already finished.
`ON CONFLICT DO NOTHING` is used so the losing transaction is not aborted by the
conflict.

The in-flight case is why "check if it already exists" is not enough as a design:
at the moment of the check, the row may not exist yet.

### What stops two *different* keys from charging twice?

Three independent mechanisms:

1. **Unique partial index** `payment_attempts_one_live_authorize`: only one
   authorize or sale per payment may be live or successful. The second insert
   fails, and the API returns the current status or 409.
2. **Conditional state update**: the payment can only move from
   `AWAITING_METHOD` to `PSP_PENDING` once.
3. **PSP idempotency**: each attempt uses its own key, so a network retry of that
   attempt returns the first answer.

CK2 checks the provider's record: **one** successful authorize or sale per
payment, **one** successful capture or void, and one effect per refund. An
authorize followed by a capture is the normal lifecycle, not a violation.

### Is the PSP call idempotent too?

Yes, by construction: `attempt_id` (or `refund_id`) **is** the PSP idempotency
key. A retry of the same operation reuses it, so the PSP returns the first answer
or completes it now. A *new* attempt gets a *new* key.

This is why the key is per operation, not per payment: two authorizes on the same
payment must be two distinct PSP operations, or the second would silently replay
the first.

### Why is fraud checked on a retry but skipped on a replay?

A replay is not a new charge attempt; it is the same request asking for its
answer. Running fraud again could block an already-approved payment. A retry with
a new key is a genuinely new attempt and must be checked, because retrying a
payment is exactly how card testing works (D33).

### What is the attempt cap for?

Ten authorize attempts per payment by default. It bounds retries against **one
payment**: cost, repeated guessing, and an infinite loop from a buggy client.
After the cap, new attempts are refused and the payment waits for its window to
end.

It is **not** a card-testing defense on its own, because an attacker can create
many payments. The full control set is layered: per-payment attempt caps,
per-merchant and per-API-key rate limits, card and customer velocity counters, IP
and device signals, and fraud decisions across payments. In this build only the
attempt cap and the in-flight limiter exist, and the fraud seam is
`AllowAllFraudCheck`, so the other layers are design, not protection.

---

## 5. `UNKNOWN`, callbacks and reconciliation

Stage 1 built the `UNKNOWN` state, the attempts it leaves behind, and the
alerting. Callback handling and the automated reconciler are designed for
Stage 2. Answers below say which is which.

### Why is `UNKNOWN` a state instead of `FAILED`?

Because a timeout tells you that you do not know, not that it failed. If you mark
it failed, the customer retries, and now two charges may exist. `UNKNOWN` is the
honest state, and it has an owner: the reconciler.

### What happens on a PSP timeout during authorize?

The attempt becomes `UNKNOWN`, the payment becomes `UNKNOWN`, and the API returns
202 with the payment id. No second attempt is created. The reconciler later asks
the PSP what happened and applies the answer: approved → `AUTHORIZED`, declined →
back to `AWAITING_METHOD`, still unknown → escalate.

### Why 202 instead of an error?

202 means "accepted, result later". It is accurate: the request was accepted and
the outcome is pending. An error code would tell the client the charge did not
happen, which is not known.

### How does the reconciler find out what happened?

Each PSP adapter declares a **recovery mode**, because providers differ:

| Mode | How it recovers |
|---|---|
| Authoritative lookup | Read the operation by our reference |
| Replay | Re-send the same request with the same idempotency key; get the first answer or run it now |
| Technical cancel | Cancel by reference; the result arrives by callback |

Callbacks can resolve an attempt under any mode. If no mode can reach an answer,
the attempt stays `UNKNOWN` and a person resolves it.

### Why does "not found" not mean "not charged"?

Because a lookup can be stale. A payment created a second ago may not appear in a
search index yet, and an outage can delay it by an hour. Failing an attempt on
"not found" would let the customer retry while the first charge is still in
flight.

So the design requires **proof** of "not charged" before failing an attempt (D27):
a quiet window plus a documented-consistent lookup, or a confirmed cancel.

### What happens if it stays `UNKNOWN` too long?

Past an age limit it moves to `REVIEW` and alerts (D45). `REVIEW` blocks new
attempts on that payment and requires two people to resolve (D41). The rule is
simple: when the system cannot decide about money, a human decides, and the
decision is recorded.

### What happens when a callback arrives twice?

Nothing, twice. `psp_events` has primary key `(psp_account_id, psp_event_id)`, so
the second insert conflicts and is ignored. The state transition is conditional,
so it cannot apply twice either. Duplicate callbacks are expected, not an error.

### What about a late approval for an attempt already marked `FAILED`?

This is the most dangerous case, because the customer may now be charged by two
attempts. It is the case the local protections cannot prevent, only contain.

The attempt moves to `REVIEW`, an alert fires, and **no ledger entry is posted**.
If the payment is waiting for a new method, it also moves to `REVIEW` so no new
attempt can start. From there, resolution is manual and recorded.

The reconciler then voids the late attempt where the PSP allows it, under its own
key. A late-approved *sale* cannot be voided, so a person reverses it at the PSP.

### What if a callback contradicts the stored state?

The conditional update changes nothing. The event is stored, and an alert fires.
That is a genuine incident — the provider and our database disagree — and it is
not something to auto-heal by overwriting state.

### Is the reconciler built?

Not yet. Stage 1 built the *visibility*: unresolved payments, attempts and refunds
are exposed through metrics and alerted when they age, with a runbook
([unknown outcomes](runbooks/unknown-outcomes.md)). The automated reconciler,
PSP sandbox and callback handling are the next stage (Stage 2).

---

## 6. Money and the ledger

### How is money represented, and why is the currency exponent part of it?

Money is an integer count of the currency's smallest unit, plus a currency code.
The currency says how many decimal places that unit implies:

| Currency | Exponent | Minor unit | $24.00 stored as |
|---|---|---|---|
| USD | 2 | cent | 2400 |
| JPY | 0 | yen | 24 |
| KWD | 3 | fils | 24000 |

Never a float: `0.1 + 0.2 != 0.3`, and a payment system cannot absorb drift.
Decimal is fine for arithmetic at the edge, but storage and the ledger hold
integers, because equality, indexing and cross-system comparison must be exact.

Rounding is data, not code: the `currencies` table carries the rounding mode
(half-even here), so adding a currency does not require a code change. Amounts in
different currencies are never summed together.

Here: `amount_minor bigint`, a `currencies` table with `exponent` and `rounding`,
and a `Money` type in the domain. FX is out of scope.

### Why double-entry bookkeeping?

Because "money in" and "money out" alone cannot express where money is. Double
entry writes every movement as matching debit and credit lines, so the books
always balance and every amount has a source and a destination. An error becomes
a visible imbalance, not a silent missing number.

### What accounts exist, and what do the flows look like?

| Event | Debit | Credit | Money | Clears when |
|---|---|---|---|---|
| Authorize | `auth_hold` | `auth_hold_contra` | Logical | Capture, void or expiry |
| Release hold | `auth_hold_contra` | `auth_hold` | Logical | Hold cleared |
| Capture | `psp_receivable` | `merchant_payable` | Real | Settlement line matches |
| Settlement | `settlement_bank` | `psp_receivable` | Real | Receivable cleared |
| Refund reserved | `merchant_payable` | `refund_reserved` | Real | Confirm or release |
| Refund released | `refund_reserved` | `merchant_payable` | Real | Reservation cleared |
| Refund confirmed | `refund_reserved` | `psp_refund_payable` | Real | Settlement line matches |
| Refund settled | `psp_refund_payable` | `settlement_bank` | Real | Payable cleared |

"Debit" and "credit" here are bookkeeping directions, not "money in/out". The
useful reading is: **who owes what to whom**.

### Why is an authorization hold "logical" money?

Because no money moved. The issuer reserved funds on the customer's card; our
books have nothing to receive yet. Recording it as logical money
(`class_of_money = 'logical'`) keeps the "one capture, one hold" story visible
without pretending a balance changed.

### Why no `balance` column on an account?

Because every capture would update the same row, and at 5,000/s that row is a
serialization point and a lost-update risk. Ledger inserts avoid that single
shared row. They still incur normal index, foreign-key, page and WAL contention,
which must be measured under load rather than assumed away.

Balances are derived: a snapshot plus the entries since it. Reporting can be
slightly stale; the entries cannot be wrong.

### How is the ledger kept balanced if the code has a bug?

A **deferred constraint trigger** checks, at commit, that for each transaction and
currency the sum of debits equals the sum of credits. Deferred means all entries
are inserted first and checked once. If they do not balance, the commit fails.

That is a backstop, not the main mechanism: the domain function produces complete
entry sets, and the application inserts each transaction in one statement.

### How are mistakes corrected?

By adding a **reversing entry**, never by editing or deleting. The application
role has `SELECT` and `INSERT` only on ledger tables; it cannot update or delete
ledger rows even if a developer tries.

This is what CK1 verifies from outside: every transaction balances, and no ledger
row was changed or removed.

### Why is the ledger written in the same transaction as the state change?

So "payment captured but not booked" is not a reachable state. If they were
separate calls, a crash between them would leave money moved with no accounting
record, and you would need a second reconciliation to repair it.

It also means the ledger write is on the hot path, which is exactly why no balance
row is updated and no expensive work happens there.

### Why 64 sub-accounts for hot accounts?

A platform fee account, or a merchant's payable account, would otherwise be one
row receiving every entry. The ledger splits such accounts into 64 sub-accounts
chosen by `hash(payment_id) % 64`, and sums them on read. Writers spread out;
readers pay a small aggregation cost.

This is a contention-reduction **strategy**, not a measured result. Its
effectiveness depends on the actual write distribution, index behaviour and the
read aggregation cost, and it has not been benchmarked on its own.

### What is a clearing account, and why must it return to zero?

A clearing account holds money temporarily while a flow is in progress
(`psp_receivable` while waiting for settlement, `refund_reserved` while waiting for
the PSP). When the flow finishes, it returns to zero. A nonzero balance past its
allowed age means a step is missing — a settlement line never arrived, a refund
never confirmed. That is why CK7 checks it.

### How do refunds stay within the captured amount?

Reserve first, with a conditional update that also enforces the invariant, and a
database `CHECK (refunded_minor + refund_pending_minor <= captured_minor)` as a
second line of defence. State changes only after the PSP confirms (D6).

A refund that times out stays `UNKNOWN`, and its reservation stays held, so it
cannot be double-spent while unresolved.

### What about fees, payouts and negative balances?

Out of scope for v1 (D18, D38). With no payouts and no retained fees, the
per-payment refund limit is sufficient and no account can go negative. Before fees
or payouts ship, the per-account balance check (D9) must come back, because then
"this merchant's payable" can genuinely go negative.

### What is the difference between business date and creation timestamp?

`created_at` is when the row was written. `effective_at` is when the money
movement happened in business terms. They differ whenever an event arrives late:
a PSP callback at 02:00 about a capture at 23:50 belongs to the earlier business
day.

That matters because settlement files, fee periods, daily reconciliation and
merchant statements are grouped by business date. Grouping by `created_at` would
move money between days and make our books disagree with the PSP for reasons that
are not errors.

Here: `ledger_transactions` carries both, and a late PSP event keeps its earlier
`effective_at`.

---

## 7. Failure and recovery

### What rules does every failure follow?

1. Money is never lost or doubled. If unsure, say unknown and find out later.
2. Save intent before acting.
3. Say "busy" quickly; do not queue without limit.
4. Every wait has a limit.
5. Every retry is safe.
6. Failure stays small (bulkheads, per-merchant limits, separate queues).
7. Recovery is gradual, not a thundering herd.

Every entry in [Failure scenarios](failure-scenarios.md) is one of these. Each has a
diagram of its recovery path, from the failure through recovery to normal operations
([Recovery flows](failure-scenarios.md#recovery-flows)).

### What happens if the process crashes at each point?

| Crash point | Database holds | Recovery |
|---|---|---|
| Before transaction A commits | Nothing | Client retries with the same key |
| After A, before the PSP call | Payment `PSP_PENDING`, attempt `PENDING` | Reconciler recovers; only proof fails it |
| After the PSP call, before B | Same | Reconciler applies the PSP's real answer |
| During a commit | Unknown | Read the row; never retry blind |

### What happens if the database primary fails over during `COMMIT`?

This is the subtlest bug found in the project (DC-01). Cancelling a commit while
PostgreSQL waits for synchronous replication makes the server return **success**
with only a warning, for a row that was committed locally but not replicated. The
driver reports success. A payment could be acknowledged and then lost on failover.

So `TxRunner` never cancels a commit. It calls `CommitAsync` with no
cancellation token, waits at most 3 seconds, and if that wait expires or a
warning appears, it throws `CommitOutcomeUnknown` and treats the outcome as
unknown. The connection is handed to a continuation that observes the late
result. Transaction A answers 503 and makes no PSP call; transaction B answers
202. Section 9 has the full durability model and the four crash boundaries.

### Why is a deadlock different from a lock timeout?

A deadlock (`40P01`) is two transactions waiting on each other; PostgreSQL detects
it and kills one, and the survivor is safe to retry. A lock timeout (`55P03`) means
a lock was held too long; retrying immediately makes it worse.

So: `deadlock_timeout` is 100 ms, `lock_timeout` is 200 ms (D1). Deadlocks become
visible and are retried at most twice. Lock timeouts return 503 with
`Retry-After`. If `deadlock_timeout` were the default 1 s, every waiter would time
out first and deadlocks would be invisible.

### What if the PSP is slow, down, or rate-limiting us?

- **Slow**: the per-call deadline (2.5 s) turns it into `UNKNOWN`.
- **Down**: a circuit breaker per PSP opens; new payments fail fast or route to a
  second PSP if the merchant has one and the method can move.
- **Rate-limited**: a client-side token bucket per PSP account plus backoff with
  jitter, and we do not create new attempt ids to "get around" it.

Breakers exist per PSP *and* per account, so a revoked credential fails one
merchant fast instead of tripping a PSP-wide breaker.

In Stage 1 only the mock adapter runs, and none of this resilience code is
wired. It is the contract a real adapter must meet, not a claim about today's
build.

### What if the broker is down?

Nothing on the money path. Events accumulate in the outbox, which is committed
with the payment. When the broker returns, the publisher (Phase 3) drains the
backlog. CK4 proves every outbox row was eventually published and had one effect
per consumer.

### What if a merchant's webhook endpoint is down?

It is isolated per endpoint, retried with exponential backoff and jitter, and
moved to a dead-letter queue with an alert after N failures. A slow consumer must
never slow payments, which is why webhooks are asynchronous and signed (HMAC with
a timestamp) so the merchant can verify them.

### What happens under overload — 15,000 req/s?

That is the L3 test: roughly 1.5× the request rate that carries the target
payment rate at the D47 mix, held for 10 minutes.

Admission control sheds the excess immediately with 429 or 503 and `Retry-After`.
The system protects correctness first, then the payment path, then refunds, then
webhooks, then reporting. Latency stays bounded because work is refused rather
than queued.

How fast it returns to SLO after the burst is the thing **L3 measures — and L3
has not been run yet.** The 2-minute recovery is the test's gate, not a result.
Do not quote it as one.

### What if a timer fires twice?

Timers are idempotent by construction: expiry and cancellation are conditional
updates from a specific state. A second run finds the state already moved and
does nothing. Timer ordering is fixed and each kind of wait has its own limit
(D34), so one stuck timer category cannot starve another.

---

## 8. Database and concurrency

### Why conditional updates instead of `SELECT ... FOR UPDATE`?

A conditional update is one atomic statement:

```sql
update payments
   set state = 'CAPTURED', captured_minor = $amount
 where id = $payment_id and merchant_id = $merchant_id
   and state = 'AUTHORIZED';
```

Zero rows updated means someone else moved it. There is no read-modify-write
window, no extra lock round trip, and no risk of forgetting to re-check the state
after acquiring the lock. `FOR UPDATE` is still used where several rows must be
touched together — then in a fixed order.

### Why unique partial indexes?

Because "only one live attempt" is a rule about **rows that exist**, and an
application check is a race. A partial unique index expresses it exactly:

```sql
create unique index payment_attempts_one_live_authorize
  on payment_attempts (payment_id)
  where operation in ('authorize','sale')
    and state in ('PENDING','ACTION_REQUIRED','UNKNOWN','SUCCEEDED');
```

Two concurrent inserts: one wins, the other fails. No coordination code.

### Why a fixed lock order?

Two transactions that lock rows A then B and B then A will deadlock under load.
The fix is boring and effective: always lock the lowest id first, using
`FOR NO KEY UPDATE` (plain `FOR UPDATE` still deadlocked through foreign-key
`FOR KEY SHARE` locks — DC-02). In the deadlock lab, this removed deadlocks
entirely instead of retrying them.

### Why hash-partition the hot tables?

At high write rates, every insert touches the same last page and the same index
leaf, so writers serialise on page locks and relation-extension locks. Hash
partitioning by `id` (32 ways for payments) spreads inserts across independent
partitions, and old data can be retired by dropping partitions.

The cost: any lookup **without** the partition key must probe every partition.
So queries always carry `id` (or `merchant_id`, for the tables partitioned by it).

### Why UUIDv7 instead of UUIDv4?

UUIDv7 is time-ordered, so new rows append at the end of the primary-key index
instead of scattering across it. Random UUIDv4 causes page splits, index bloat and
more WAL. For a system whose whole point is write throughput, that matters.

### Why a deferred constraint trigger rather than an audit job?

An audit job finds an unbalanced transaction *after* it committed; a trigger
prevents the commit. Deferred means the check runs once per transaction at commit,
after all entries are inserted, so it costs one query per transaction rather than
one per row. Its cost is measured in L1; if it is too expensive, a continuous
audit query replaces it and property tests remain the primary guard.

### Why PgBouncer, and what is the catch?

Thousands of API connections would exhaust PostgreSQL's connection limit, and
each backend process costs memory. PgBouncer in transaction-pooling mode keeps a
small fixed pool per cell (starting around 100) and multiplexes clients onto it.

The catch: transaction pooling keeps no session state. Session-level features
(advisory locks, `SET`, prepared statements) need care. Protocol-level prepared
statements are supported from PgBouncer 1.21, but that must be verified with
Npgsql before relying on it.

### How are N+1 queries prevented?

Query-count budgets as tests: each endpoint has a maximum number of statements,
and CI fails if it grows (DC-07). Hot queries also have `EXPLAIN` plan checks
against a representative dataset. Both exist because the first version of the
budgets had no basis and the first refund lookup scanned the whole table.

### Which isolation level do I need, and what does each one buy?

PostgreSQL defaults to READ COMMITTED. Three levels matter here:

| Level | What it gives | What it costs |
|---|---|---|
| READ COMMITTED | Each statement sees a snapshot as of statement start; `SELECT FOR UPDATE` blocks, then reads the latest committed row | Two statements can see different snapshots, so check-then-write needs a lock or a conditional update |
| REPEATABLE READ | One snapshot for the whole transaction. A lost update to the same row is prevented by **aborting** the stale transaction | Concurrent writes to the same row raise `40001`; retry the whole transaction |
| SERIALIZABLE | Serializable snapshot isolation (SSI); detects dangerous interleavings | More `40001` under contention; retry is mandatory |

The practical rule: prefer one atomic statement — a conditional `UPDATE`, an
`INSERT ... ON CONFLICT`, a unique index — over "read, decide, write". Then the
isolation level matters far less. When a multi-statement decision is unavoidable,
lock in a fixed order and retry `40001`.

Here: transitions are conditional updates, the state change and ledger insert are
one transaction, and retries are bounded at two for `40001` and `40P01`.

### What is write skew, and where would it bite here?

Write skew is two transactions that each read a state the other is about to
invalidate, then both write a change that is individually valid and collectively
wrong. Snapshot isolation does not prevent it, because neither transaction writes
the row the other read.

The refund is the classic case. Captured is 100. Refund A reads refunded = 0 and
decides 60 is allowed. Refund B reads refunded = 0 and decides 60 is allowed.
Both insert a 60 refund. The total is 120, and no row was updated twice.

Three defenses, in order of preference: put the invariant in the write
(`where refunded + pending + amount <= captured`), back it with a `CHECK`
constraint, and lock the parent row when neither is possible.

Here: the refund reserve step is exactly that conditional update, the `CHECK`
constraint is the backstop, and CK3 checks the invariant after the fact.

---

## 9. WAL, durability and crash recovery

A payment is not accepted when the row is inserted. It is accepted when the
commit is durable: WAL flushed locally, and — when synchronous replication is
configured — acknowledged by the required standby. Everything below follows from
that sentence.

### What is WAL, and why does it decide whether a payment can be acknowledged?

WAL (write-ahead log) is an append-only record of changes. PostgreSQL writes a
change to WAL and flushes it **before** the modified data pages reach the main
files:

```
payment transaction
      |
      v
WAL records appended to WAL buffers
      |
      v
WAL written and flushed to disk (fsync)
      |
      v
commit acknowledged   <-- the moment the payment is really recorded
      |
      v
dirty data pages written later by the checkpointer
```

That is why a commit is fast without writing the page, and why a crash is
recoverable: replay the WAL. The same records feed replication, point-in-time
recovery and logical decoding.

The rule for the API follows directly: **never answer "success" before the commit
returns.** The database is the source of truth, and the WAL flush is what keeps
that true after a power loss.

### Which WAL names are diagnostics, and which are settings?

Confusing the two is the usual mistake. Wait events tell you what a backend is
waiting for; settings change behaviour.

| Kind | Names | Use |
|---|---|---|
| Wait events and locks | `WALWrite`, `WALSync`, `SyncRep`, the `WALWriteLock` lightweight lock | Read them in `pg_stat_activity` to find a bottleneck. They are not tunables |
| Settings | `wal_level`, `wal_buffers`, `wal_sync_method`, `full_page_writes`, `wal_compression`, `max_wal_size`, `min_wal_size`, `checkpoint_timeout`, `checkpoint_completion_target`, `wal_keep_size`, `max_slot_wal_keep_size` | Change deliberately, with a measurement |
| Commit semantics | `fsync`, `synchronous_commit`, `synchronous_standby_names` | These decide whether "committed" survives a crash. Never trade them for throughput |

A full WAL buffer is not a wait event you tune; it appears as `WALWrite` waits
and as the `wal_buffers_full` counter in `pg_stat_wal`.

### What do the durability settings actually guarantee?

| Setting | What it guarantees | The trap |
|---|---|---|
| `fsync = on` | WAL flushes reach durable storage | `off` risks **unrecoverable corruption** after a crash, not just lost recent commits |
| `wal_level = replica` | Enough WAL for replication and PITR | `minimal` omits information replication needs |
| `synchronous_commit = on` | Local WAL flush, plus the required standby ack **if** synchronous standbys are configured | On a primary with no synchronous standbys, `on` is local flush only. That is not RPO 0 |
| `remote_write` | The standby has written WAL to its operating system | Not fsynced on the standby; a standby power loss can still lose it |
| `remote_apply` | The standby has replayed the change | Slowest, and a slow apply on the standby stalls commits |
| `local` | Local flush only, synchronous standbys ignored | Fine for non-money work; never for payments |
| `off` | No wait at all | A window of acknowledged-but-lost commits |
| `synchronous_standby_names` | Which standbys count for the ack | `ANY 1 (s1,s2)` is a quorum of any one; `FIRST 1 (s1,s2)` is the first available in list order |
| `full_page_writes` | Protection against torn pages after a crash | The first change to a page after a checkpoint writes the whole page to WAL, so WAL volume rises |
| `max_wal_size`, `checkpoint_timeout` | How often checkpoints run | More checkpoints mean more full-page WAL and I/O spikes; fewer mean longer recovery |
| `checkpoint_completion_target` | Spreads checkpoint writes | Too low causes latency cliffs around checkpoints |
| `wal_compression` | Smaller WAL records | Costs CPU; measure before enabling |

RPO 0 is a property of the whole deployment, not of one setting. This project
targets it, which requires all of the following:

- `fsync = on`, `synchronous_commit = on`, and a synchronous quorum (D7, D28).
- A **fenced** old primary that cannot continue accepting writes.
- Promotion only of a standby that holds the acknowledged WAL.
- Refusing to acknowledge writes under a degraded configuration that no longer
  satisfies the policy.

Lose any one of them and the honest claim is "RPO 0 for failovers that follow
this procedure", not "a commit can never be lost". Section 16.3 states what the
topology is and what has actually been tested.

### What is synchronous replication actually waiting for?

```
                      Payments API
                           |
                           v
                       Primary DB
                           |
                     append WAL
                           |
              +------------+------------+
              |                         |
              v                         v
         Standby 1                  Standby 2
       write + fsync              write + fsync
              |                         |
              +------------+------------+
                           |
                    quorum satisfied
                           |
                           v
                   commit acknowledged
```

The commit waits for the configured quorum, not for "all standbys". With
`ANY 1 (s1,s2)`, losing one standby keeps commits flowing. With `FIRST 1 (s1,s2)`,
commits use the first standby in the list, so losing that one changes behaviour.

What synchronous replication does **not** do:

- It does not protect against a bug that deletes the wrong rows.
- It does not protect against corruption that reaches every node.
- It does not make failover safe by itself: the old primary must be fenced so it
  cannot accept writes, and only a standby that has the WAL may be promoted.
- It does not protect a single-region deployment from losing the region.

### What does a commit cost, and where do p99 spikes come from?

A commit is roughly: WAL insert, local flush (`fsync`), a network round trip to
the synchronous standby, the standby flush, and any queueing in front of the WAL
writer. Concurrent commits are batched into one fsync by **group commit**, which
is automatic.

So the fixed cost per commit is a disk flush plus a network round trip. At 5,000
payments/s, a payment that needs two commits pays it twice. "How many commits per
payment" is a capacity question, not a style question.

p99 spikes usually come from one of these:

| Cause | Signal |
|---|---|
| Checkpoint flushing dirty pages | checkpoint counters, `pg_stat_io` write time, a periodic latency sawtooth |
| `fsync` on slow or contended storage | `pg_stat_wal.wal_sync_time`, `WALSync` waits |
| Standby acknowledgement delay | `SyncRep` waits, `pg_stat_replication.flush_lag` |
| WAL buffer exhaustion | `wal_buffers_full`, `WALWrite` waits |
| Lock waits before commit | `pg_locks`, `pg_stat_activity.wait_event` |
| Poor group-commit batching at low concurrency | latency high, throughput low, no storage pressure |

`commit_delay` and `commit_siblings` trade latency for batching, but they are
measured in microseconds and usually make p99 worse on modern storage. Measure;
leave them off.

### Why must a commit never be cancelled?

Because a cancelled commit can have an **ambiguous durability outcome**. In this
project's reproduction (DC-01: PostgreSQL, synchronous replication with no standby
available, Npgsql), cancelling a commit while it waited for a standby made the
server return a successful local commit with only a warning, and no required
standby durability. The driver reported success. The payment was acknowledged and
would have been lost on failover.

That is an observed failure mode of **this stack**, not a claim that every
cancelled PostgreSQL commit behaves the same way. It is enough to ban cancelling a
commit outright.

So `TxRunner` follows five rules:

- `Command Timeout = 0`, checked at startup, so the driver never injects a
  cancel.
- `CommitAsync` is called with no cancellation token.
- Wait at most 3 seconds for the result.
- On timeout, a `WARNING` notice, or an exception during `COMMIT`, throw
  `CommitOutcomeUnknown`.
- Keep the connection until the real outcome is known, and never retry blind.

`Command Timeout = 0` removes the driver's timeout for **every** command, not only
`COMMIT`, so it must not be copied without the bounds that replace it. Here,
ordinary statements are bounded server-side by `statement_timeout` (1 s) and
`lock_timeout` (200 ms), request work is bounded by the request deadline, and the
commit wait has its own 3-second bound. Without those, `Command Timeout = 0` means
unbounded operations.

The API answer depends on which transaction was interrupted. If transaction A is
unknown, answer 503 and **do not call the PSP**. If transaction B is unknown, the
PSP has already answered, so answer 202 with the current state.

This is also why a client disconnect does not cancel the PSP call or the commit:
the money may already have moved.

### What is a commit outcome unknown, and how is it different from `UNKNOWN`?

`UNKNOWN` is a payment state: we do not know what the PSP did. A commit outcome
unknown is an infrastructure state: we do not know whether our own commit
succeeded. Both follow the same rule — do not guess — but they recover
differently. A commit unknown is resolved by reading the row back or waiting for
the abandoned commit to finish. A PSP `UNKNOWN` is resolved by asking the
provider.

### How do I reason about a crash at each boundary of the commit path?

Use the four boundaries below and ask the same five questions each time: does the
transaction exist on the primary, on the standby, does it survive failover, what
does the client observe, and can a retry create a duplicate?

**Boundary 1 — before the local WAL flush.** The transaction exists nowhere. The
client got no answer. A retry with the same key finds nothing stored, so it runs
cleanly. No duplicate is possible because no effect existed.

**Boundary 2 — after the local flush, before the standby acknowledges.** The row
exists on the primary and is visible there, but not on the standby and not
acknowledged to the client. If the primary fails over now, the row is lost.
Whether that is dangerous depends on **which transaction** it was:

- If it was A (the intent), no PSP call has happened, because the API only calls
  the PSP after A commits. Losing A loses no charge.
- If it was B (the result), the charge exists at the PSP, but A is already
  durable on the quorum, so the attempt id survives and the reconciler can ask
  the PSP what happened.

This is the strongest argument for intent-before-action: **A must be
quorum-durable before the external call.** Otherwise a failover can destroy the
only reference to a charge that really happened.

**Boundary 3 — quorum durability is achieved, but the database driver has not
returned success to the application.** The WAL is on the required standbys, so
the transaction survives a correctly fenced failover to a standby that holds it.
The backend is still waiting: for the standby acknowledgement to propagate, or
for the driver to return. The client sees a timeout. A retry with the same key
finds the row; if the first request is still marked in flight it gets 409,
otherwise the stored answer. No duplicate effect.

**Boundary 4 — the application received successful commit completion, but the
HTTP response is lost before the client receives it.** This is what C52 injects.
The commit is durable and the application knows it; only the last network hop
failed. The retry must return the original payment id and outcome, because the
idempotency row now holds a resolved answer.

| Boundary | Primary | Standby | Survives a fenced failover | Client sees | Retry can duplicate? |
|---|---|---|---|---|---|
| 1. Before local flush | no | no | n/a | error | no — nothing was stored |
| 2. After local flush | yes | no | no | error or timeout | no — if A was lost, no PSP call had happened; if B was lost, A still holds the key and attempt |
| 3. Quorum durable, driver not returned | yes | yes | yes | timeout | no — the same key replays the answer |
| 4. App has commit success, response lost | yes | yes | yes | timeout | no — the same key replays the answer |

Doing this on paper once is worth more than reading a chapter, because it
connects WAL, replication, commit cancellation, idempotency and crash recovery
into a single model.

### What are the WAL performance bottlenecks, and what do I measure?

| Bottleneck | What to measure | What usually helps |
|---|---|---|
| Storage flush latency | `pg_stat_wal.wal_sync_time`, `pg_stat_io` | Faster storage, WAL on its own volume |
| WAL write contention | `WALWrite` waits, `wal_buffers_full` | Raise `wal_buffers` modestly; reduce WAL volume |
| Standby acknowledgement | `SyncRep` waits, `flush_lag` | Network path, standby storage, quorum choice |
| Checkpoint pressure | `pg_stat_checkpointer` (checkpoint counters moved there in PostgreSQL 17; this project runs 18) plus `pg_stat_io` | `max_wal_size`, `checkpoint_completion_target` |
| WAL volume per transaction | `wal_bytes` per payment | Fewer index writes, `wal_compression`, smaller rows |
| Commit rate | commits/s, group-commit batching | Fewer commits per payment where the semantics allow |
| Replication slot retention | slot lag, disk usage | `max_slot_wal_keep_size`, alerting |

Timing columns need instrumentation: `pg_stat_wal`'s `wal_sync_time` and
`wal_write_time` stay at zero unless `track_wal_io_timing` is on. Check it before
drawing conclusions from them.

The project rule stands: never improve throughput by weakening the durability
guarantee. A faster system that loses an acknowledged payment is not a payment
system.

---

## 10. Performance and overload

### What exactly does p99 ≤ 100 ms measure?

**Platform overhead**: request time minus time spent waiting for the PSP. It is
the part we control. End-to-end time is recorded separately but is not a gate
until a real PSP target exists, because the PSP's latency is not ours.

The metric is `op_platform_overhead_seconds`, and its histogram buckets cluster
between 25 ms and 100 ms on purpose.

### Is 5,000 payments/s proven?

No. It is a target. What exists is:

- A laptop limit search with valid ramp steps (preview only).
- A laptop milestone of 500 completed payments/s, as a preview (D51).
- A recorded plan for proving capacity on a Linux host (D53, D56).

Quoting the target as a result is the fastest way to lose credibility in a
review, so every document says "target" or "preview" explicitly.

### Why are laptop numbers not capacity?

Because generator, database and API share one machine, so they compete for CPU,
memory bandwidth and disk. Numbers are useful for **comparing before and after** on
the same machine, not for saying "this will do X in production". A Linux host with
separate generator and database tiers is required.

### Why is 15,000 req/s a goal if the system sheds load?

Because surviving overload is a separate property from throughput. At 15,000 req/s
the system must keep 5,000 completed payments/s, reject the rest quickly, stay up,
and recover within 2 minutes. Systems that "handle" overload by queueing
eventually fail at the worst moment (D4).

### How many requests are in flight at 5,000/s?

Little's Law: in-flight = rate × latency. At 5,000/s and a 1-second average PSP
latency, about 5,000 requests are in flight; at 2 seconds, about 10,000. That
number sizes the concurrency limit, the thread budget and the memory estimate —
which is why the limit is explicit and shedding is immediate.

### What can the mock PSP not prove?

Real latency distribution, real error taxonomy, real idempotency-key retention,
real rate limits, real 3DS flows, real settlement files, and real provider
semantics. It proves our side of the contract. Adapter contract tests against a
sandbox come with Stage 2.

### What is the D47 mix?

The benchmark's workload mix: 20% of payments are two-call (authorize then
capture), the rest are one-call sales, plus refunds and status reads in fixed
proportions. It matters because "5,000 payments/s" converts to a different req/s
depending on the mix. The 20% is an assumption, to be replaced by real merchant
data (D47).

### What are the benchmark milestones, and what does each one prove?

| Milestone | What it is | What it does not prove |
|---|---|---|
| 500 completed payments/s | A laptop milestone (D51), with generator, API and database on one machine | Nothing about production capacity: the tiers compete for CPU, memory and disk |
| 5,000 payments/s | The capacity objective, on separate tiers with durable storage | Not proven today; it needs a Linux host and the D56 plan |

Two unit traps:

- **req/s is not payments/s.** One payment can be one call or two, so "5,000"
  means very different work depending on the mix (D47).
- **"500" here is completed payments per second**, not 500 HTTP requests. A
  500 req/s test and a 500 payments/s test are different experiments.
- Both milestones use the attempt-based definition from section 1. Until a
  business-payment metric exists, read "payments" as "completed attempts".

A run that drops arrivals cannot establish the requested rate. Before quoting any
number, check the actual arrival rate, dropped iterations and VU exhaustion,
completed throughput, p99 platform overhead, and whether CK1–CK6 passed after the
run. Section 16.1 has the recorded numbers and the unit each one uses.

---

## 11. Security, tenancy and compliance

### Is card data ever stored?

No. Card fields are hosted by the PSP (an iframe or redirect), and this system
receives a PSP token plus non-sensitive method details. There is no vault in v1
(D39). The database's check constraint enforces that an attempt has a method
reference, not card fields.

### What does that mean for PCI scope?

Much smaller, but not zero. PSP-hosted fields normally put a merchant in SAQ A
territory, but that is an **assumption pending an assessor's confirmation**, and
it does not cover logs, support tools or error trackers, which are the usual
places card data leaks.

### How are API keys stored?

As SHA-256 hashes. The key itself is never stored, so a database leak does not
leak usable credentials. Each key resolves to one merchant and one mode
(live/test).

### How is tenancy enforced?

`merchant_id` is on every tenant-owned table from the first migration (D8), and
it is part of the primary or unique key wherever partitioning requires it. Every
query filters by the authenticated merchant. There is no cross-tenant read path,
including in the staff console.

### Who can move money, and what stops abuse?

The staff console is a separate deployable that **cannot write money tables**
(D40). Money actions go through the API, with staff roles, MFA, a recorded reason,
and — for `REVIEW` resolutions, which are irreversible judgement calls — **two
people** (D41). Audit rows record intent and outcome separately (D42).

### What is audited?

Every money action has an intent record (who asked, what, why) and an outcome
record (what the system did). The ledger is append-only, so the money history
cannot be rewritten. Console sign-ins carry MFA claims, and the console accepts
MFA shown by either `acr` or `amr` (D55).

### What is explicitly not covered?

Legal, banking and card-industry compliance work; a formal PCI assessment; real
merchant onboarding (KYC); fraud strategy beyond a fast rules check; tax; and
dispute/chargeback operations. The README lists this as a limit, not a roadmap
promise.

---

## 12. Verification and evidence

### What is the difference between designed, implemented and proven?

| Document | Question it answers |
|---|---|
| This FAQ | What does the system mean, and why is it built this way? |
| [Codebase structure](architecture/codebase-structure.md) | Where does each capability live, and what is actually implemented? |
| [Implementation guide](implementation-guide.md) | How do I build, configure and run it? |
| [Verification](verification.md) | What has actually been proven, and by which run? |
| [Decisions](decisions.md) | Why was this trade-off chosen, and what does it cost? |

The rule: **never describe a designed capability as implemented, or an
implemented one as verified.** They are different claims, and mixing them is how a
project loses trust in review.

Six statuses are enough to describe almost anything here:

| Status | Meaning |
|---|---|
| **Designed** | The architecture or contract exists; no code runs it |
| **Implemented** | Code exists, not necessarily exercised under fault |
| **Unit/integration verified** | Tests exercise it and pass |
| **Fault verified** | Failure injection shows the behaviour under a real fault |
| **Load verified** | Proven under a stated workload and hardware, with correctness checks passing |
| **Production verified** | Proven in a representative deployment with real traffic |

Applied to this repository:

| Capability | Highest status reached |
|---|---|
| Two-transaction flow, idempotency, ledger | Fault verified for crash points (K1–K3); integration verified elsewhere |
| Correctness checks CK1–CK10 | Unit/integration verified; they pass on recorded runs |
| Reconciler, callbacks, outbox publisher, settlement ingestion | Designed |
| Real PSP adapters | Not implemented; the only adapter is the mock |
| Fraud rules | Designed; the implementation is `AllowAllFraudCheck` |
| 5,000 payments/s capacity | Not load verified; laptop previews only |

A passing unit test is not a durability proof. A green load run without correctness
checks is not a money proof. The status column is what keeps those apart.

### What are CK1–CK11?

Independent correctness checks, written as separate code from the payment path, so
a bug in the payment code cannot hide inside its own check:

| Check | Proves |
|---|---|
| CK1 | Ledger balances, and no ledger row was changed or deleted |
| CK2 | One successful authorize or sale per payment, one successful capture or void, one effect per refund, at the PSP |
| CK3 | Refunds never exceed captures; pending equals unresolved refunds |
| CK4 | Outbox rows are internally consistent and complete. **Scope:** it is a static check; delivery and consumer effects need a publisher, which is Phase 3 |
| CK5 | Nothing unresolved past its threshold |
| CK6 | No unbounded growth in queues, outbox age or consumer lag |
| CK7 | Clearing accounts return to zero |
| CK8 | Settlement inputs arrived within their window. **Scope:** not applicable until settlement ingestion exists (Stage 2) |
| CK9 | Every source record has its ledger match, both ways |
| CK10 | Independent records agree (e.g. `captured_minor` vs ledger sum) |
| CK11 | What the client saw matches the state machine (D52) |

Each must fail on injected corruption **and** on zero rows, or it is not a check.
A check that does not apply reports n/a, never a pass. CK1–CK10 are implemented;
CK11 is defined and runs only when the fault driver is present (D52).

Some scopes are narrower than their names suggest: CK4 is a static consistency
check until the publisher exists, and CK8 cannot run before settlement ingestion.
The table says so per row, because a check that cannot observe the thing it names
is evidence of something else. Section 16.5 lists which checks are executable
today.

### What are K1–K3?

Crash tests against the real API, not a simulation:

- **K1**: crash after the PSP charged, before recording → the attempt is findable.
- **K2**: crash during a late commit, after the PSP answer → the committed outcome
  is kept.
- **K3**: after a restart, a same-key retry of an unknown payment never calls the
  PSP.

### What are L1–L4 and the C-tests?

- **L1** ramp to 5,000 payments/s with gates at each step.
- **L2** 24-hour soak.
- **L3** 15,000 req/s overload survival.
- **L4** recovery after failures.
- **C-tests** are fault-injection tests (C34 deadlock race, C43 commit
  cancellation, C52 lost HTTP response, C55 retry during a PSP wait, and more).

### What is a "gate"?

A check that must pass before the next stage is allowed. Like a unit test, but
across the whole system: throughput, latency, correctness checks, unresolved
counts. A gate is not "we looked at a dashboard once"; it has a recorded run id.

### How do I run it locally?

See [Implementation guide](implementation-guide.md) for build and run, and the
[load harness guide](../benchmarks/README.md) for a benchmark step. Local stacks
are in `deploy/compose/`, and the two-API stack used for step 6 is
`deploy/compose/two-api.yaml`.

---

## 13. Status, scope and why things are the way they are

### What is Stage 1 and what is Stage 2?

**Stage 1** (built): the payments core, API, mock PSP, correctness checks, load
harness, staff console, crash tests.

**Stage 2** (next): PSP sandbox integration, the reconciler, callback handling,
and the design gate for settlement ingestion.

The split is deliberate: prove the money semantics with a mock before paying for
provider access.

### Why is there no payout, fee or dispute code?

Because they are not needed to prove the core claim, and each adds a large amount
of accounting surface. Payouts need a fee model, negative balances and a
settlement ledger; disputes need evidence workflows and timelines. Building them
half-way would be worse than not building them.

### Why does the repo number decisions like D47?

Because a payment system is mostly a chain of trade-offs, and "why is this 200 ms
and not 2 s?" must be answerable a year later. Each decision record states the
choice, the reason, the consequence, and its status (`Committed`, `Proposed`,
`Open`). The doc is [Decisions](decisions.md).

### What is the single most important design rule?

**The database is the source of truth, and intent is saved before action.** Every
other mechanism — idempotency, `UNKNOWN`, the reconciler, the outbox, the ledger —
exists to make that rule survivable when something crashes.

### Where should I start reading?

Architecture first, then payment lifecycle, then the failure scenarios. The
README lists the full order.

---

## 14. Where this lives in the code

The sections above explain *why*. This one says *where*. For project boundaries
and dependency rules, see [Codebase structure](architecture/codebase-structure.md).

| Area | Code | What it does today |
|---|---|---|
| Money, states, commands, rules | `src/OpenPayments.Domain/` | `Decide.cs` is the pure state machine; `Postings.cs` builds ledger entries and events; `Money.cs`, `States.cs`, `Ids.cs`. No I/O, no dependencies |
| The money writer | `src/OpenPayments.Transitions/PaymentStore.cs` | Applies a decision: conditional state update, ledger insert and outbox row in one transaction |
| Commit policy | `src/OpenPayments.Transitions/TxRunner.cs` | Commit wait limit, never cancel, retry `40P01` at most twice, lock-timeout mapping, `CommitOutcomeUnknown` |
| HTTP surface | `src/OpenPayments.Api/Program.cs` | Routes, API-key auth, request size and connection limits |
| Payment sequencing | `src/OpenPayments.Api/PaymentFlow.cs` | The two transactions, idempotency cases, PSP call, answer mapping — the file to read for the real flow |
| Auth, admission, fraud seam | `src/OpenPayments.Api/Services.cs` | API-key lookup, `InFlightLimiter`, `IFraudCheck` |
| Metrics | `src/OpenPayments.Api/Metrics.cs` | `op_*` metrics, platform-overhead and PSP-wait histograms |
| PSP boundary | `src/OpenPayments.Psp/Contracts.cs` | `IPspAdapter`, `PspResult`, `PspInquiry`, capability and recovery declarations |
| PSP implementation | `src/OpenPayments.Psp/MockPspAdapter.cs` | HTTP adapter to the mock PSP; the attempt/refund id is the idempotency key |
| Schema | `db/migrations/0001..0003` | Payments, attempts, ledger, outbox, idempotency keys, console tables |
| Correctness checks | `tools/OpenPayments.Checks/` | CK1–CK10 as raw SQL, independent of `src/` |
| Mock provider | `tools/OpenPayments.MockPsp/` | A fake PSP with an effect log, used by tests and benchmarks |
| Tests | `tests/` | One project per kind of proof: domain, integration, crash (K1–K3), checks, mock PSP, console, browser, acceptance, analyzers |
| Load harness | `benchmarks/` | k6 mix, profiles, `run.sh`, reports, recorded runs |
| Local stacks | `deploy/compose/`, `deploy/native/` | Replicated PostgreSQL, PgBouncer, two-API stack, observability |

**Stubbed or absent in the code, by design of the stage:**

- **Fraud** is an interface with `AllowAllFraudCheck`. The 50 ms budget, block
  path and 402 answer are designed, not exercised.
- **Admission control** is an in-flight cap. The token-bucket rate limiter is not
  in the API.
- **Resilience around the PSP** — breaker, per-account limiter, retry policy — is
  designed in [Architecture](architecture.md#psp-integration). The only adapter
  is the mock.
- **No callback receiver, reconciler, outbox publisher, settlement ingestion or
  real PSP adapter.** Their tables exist; their processes do not.

**Dependency rules that prevent whole classes of bug:** `Domain` cannot do I/O,
so the rules are testable in isolation. `Transitions` does not reference `Psp`,
so a PSP call inside a money transaction cannot compile. `Psp` does not
reference Npgsql, so an adapter cannot write money state. `Checks` references no
`src/` project, so a check cannot reuse the code it is checking.

**Read the code in this order:** `Decide.cs` → `PaymentStore.cs` → `TxRunner.cs`
→ `PaymentFlow.cs` → `Program.cs` → `MockPspAdapter.cs`.

---

## 15. How should I prioritise learning this?

If you are learning the domain rather than this repository, this is the weighting
I would use. It is a study order, not a claim about where the bugs are.

| Area | Weight | Why it matters |
|---|---|---|
| Financial correctness and the ledger | 20% | If the books are wrong, nothing else counts |
| Idempotency and payment lifecycle | 15% | Duplicate charges are the failure customers never forgive |
| Database and concurrency | 15% | Most money bugs are races, not syntax |
| WAL and durability | 15% | A commit that does not survive a crash is not a payment |
| Replication and recovery | 10% | Failover is where durability claims are tested |
| PSP integration | 10% | Provider contracts differ, and `UNKNOWN` is normal |
| Security and compliance | 10% | PCI scope, tenancy and audit are pass/fail |
| Outbox and reconciliation | 5% | The bridge between one database and the outside world |
| **Total** | **100%** | |

Three cautions:

- **Weights are not effort.** A 20% topic can take half the study time.
- **Security is not a rounding error.** It is 10% here, not 5%, because card data,
  tenant isolation and two-person review are gating questions in a real review.
- **Order beats weight.** Study in this order: ledger → idempotency → database →
  WAL → replication → PSP → outbox and reconciliation → security. It broadly
  follows the sections above, with the provider and messaging material later
  because it builds on the database model.

Two more labels help separate transferable knowledge from local choices:

| Statement in this document | Kind |
|---|---|
| Integer minor units with a currency exponent | Universal financial engineering |
| Intent before action | Core distributed payment principle |
| At-least-once plus idempotent effects | Universal distributed-systems principle |
| Ledger written in the same transaction as state | Universal within one database |
| `409` for an in-flight key, `422` for a body mismatch | Project API decision |
| 24-hour key retention | Project policy, tied to the provider's window |
| 3-second commit wait | Project implementation choice |
| 10 authorize attempts per payment | Project policy |
| p99 ≤ 100 ms excluding PSP time | Project SLO |
| Two people to resolve `REVIEW` | Project control |
| 64 sub-accounts per hot account | Design hypothesis, not a measured result |

If you can explain the four commit boundaries in section 9 and the balance
invariant in section 6, you can defend most of this system without notes.

---

## 16. Answers to six review questions

Short, direct answers to a reviewer's checklist. Canonical detail lives in the
sections named; this section states what is guaranteed, what is decided, and what
is measured.

### 16.1 What does the benchmark actually measure?

**Completed PSP attempts per second — option B.** Not unique payments, not HTTP
requests.

- The reported number is the k6 counter `k6_payments_completed_total` rate: one
  increment per attempt that ends authorized, captured, or declined.
- `benchmarks/run.sh` also recounts from the database (successful sales,
  authorized-and-captured payments, successful authorizes, and failed attempts
  excluding `fraud_blocked` and `timeout`), and `benchmarks/report.py` reconciles
  it against `op_payments_completed_total`.
- The D47 mix means about 0.5 completed attempts per HTTP request, so 500
  completed/s needs roughly 1,000 req/s offered.

There is no `completed_payments_per_second` metric by distinct payment id. The
review's recommendation to track unique payments, attempts and HTTP 2xx
separately is right; only the attempt metric exists today.

One inconsistency to know about: k6 also counts a `402` fraud block, while
`op_payments_completed_total` excludes it. Recorded runs use `AllowAllFraudCheck`,
so that path is not exercised.

Evidence: step 5 at 500 req/s offered recorded **248.32 completed/s**; step 6 at
~1,000 req/s recorded 498.33 but is invalid on count. The afternoon repeat reached
500.03 completed/s with **p99 173 ms**, over the gate. D51's laptop milestone of
500 completed/s needs three valid 10-minute windows and has not been met.

**Status:** measured; milestone not met.

### 16.2 What is the duplicate-charge guarantee after timeout → `UNKNOWN` → key expiry → retry?

The guarantee rests on **local attempt identity and the state machine**, not on
the PSP key.

1. The timeout leaves the attempt `UNKNOWN`. The partial unique index
   `payment_attempts_one_live_authorize` includes `UNKNOWN`, so no new
   authorize or sale can be inserted for that payment. A retry gets the current
   status, or 409 while the first request is still running.
2. The payment cannot be canceled: cancel is only allowed from `AWAITING_METHOD`.
3. The attempt is never marked `FAILED` without proof (D27), so the window stays
   open instead of being silently closed.
4. Once the provider's key has expired, recovery uses an authoritative lookup or
   a technical cancel. Replay with the same key is allowed only well inside
   retention.
5. The outcomes are: approved → `AUTHORIZED`, so there is nothing to retry;
   proven not charged → `FAILED`, the payment returns to `AWAITING_METHOD`, and a
   retry is safe; inconclusive → `REVIEW`, which blocks new attempts and needs two
   people.

So the protection is permanent local attempt identity + one-live-attempt
constraint + proof-only failure + reconciliation. PSP idempotency is the second
layer, and it protects a retry of the **same operation**, not a customer's new
attempt.

Residual risks: the merchant creating a **new payment id**; a charge made
out-of-band at the PSP; or a "proof" that is wrong. And the reconciler is not
built, so today this is design plus constraint tests, not an end-to-end
demonstration.

**Status:** constraint implemented; end-to-end recovery designed, not built.

### 16.3 What does D7 guarantee during failover?

**Designed:** two synchronous-capable standbys in different zones with quorum
`ANY 1`. The primary waits for one acknowledgement, so losing one zone does not
block commits and does not give up RPO 0. Failover is designed as Patroni or a
managed multi-AZ service that promotes a standby holding the acknowledged WAL,
with the old primary fenced before promotion.

**What is true today:** D7 is a `Committed` design decision, not a tested
mechanism. There is no failover controller, no promotion test and no fencing test
in this repository; the local stack runs replicas without orchestration.

So RPO 0 is a requirement under "a correctly fenced failover to a standby that
holds the acknowledged WAL". It is not a demonstrated result.

**Status:** designed; not fault verified.

### 16.4 What exactly did DC-01 observe?

Environment: PostgreSQL 18.6, Npgsql 10.0.3, PgBouncer 1.26.1,
`synchronous_standby_names` naming two standbys that do not exist. Run twice,
same results.

- **Local commit:** yes, the row was committed and visible on the primary.
- **Standby:** none existed, so no standby received or flushed that WAL.
- **Client:** with `Command Timeout=1`, a normal success after about 1 s plus a
  `WARNING 01000` notice. With `Cancellation Timeout=-1`, an exception instead.
  A `pg_cancel_backend` from outside also produced the warning-success.
- **Without cancellation:** wait bounded by `WaitAsync`, no cancel sent, row
  invisible until the commit finished, then late success and a reusable
  connection.

Reproduced consistently across two probe runs and several cancellation paths.
**Not** reproduced through a real crash or failover. C43 exercises the no-cancel
path against a stalled `SyncRep` wait, direct and through PgBouncer — a stall, not
a promotion.

**Status:** observed and repeatable on this stack; not a general PostgreSQL
guarantee.

### 16.5 Which correctness checks are executable today?

| Check | Runs on | Today |
|---|---|---|
| CK1–CK3, CK5, CK6, CK9, CK10 | SQL and the PSP effect log | Runnable, and pass on recorded runs |
| CK4 | SQL over `payments` and `outbox` | Runnable, but **database-only** |
| CK7, CK8 | SQL over clearing and `settlement_lines` | Not applicable before Phase 2 |
| CK11 | Needs the fault driver | Defined; not in a normal run |

CK4 checks three things only: every payment's `event_seq` has an outbox row,
every outbox row refers to a payment, and no outbox sequence exceeds
`event_seq`. It does **not** verify publication or consumer effects — there is no
publisher yet.

CK8 checks whether any settlement line is older than 24 hours. It does **not**
verify ingestion; nothing fills that table yet. In the recorded baseline CK7 and
CK8 are reported as n/a, never as passes.

**Status:** CK1–CK6, CK9, CK10 implemented and run; CK4 static; CK8 blocked on a
missing process.

### 16.6 What is the latest authoritative L3 result?

**There is none in this repository.** The Phase 1 gate records L3 as "Not run,
needs the stable rate", and the open-experiments list schedules it for Phase 4.
D56's laptop verification plan includes L3, but the run has not happened.

So the figures of a 430–660 ms recovery tail, 3,070 dropped arrivals and a CK5
failure cannot be confirmed or denied from this repository, and this FAQ does not
cite them. If that run exists, it needs a run directory and id under
`benchmarks/runs/` before it can be used as evidence; then
[Verification](verification.md) and this FAQ can reference it.

**Status:** not run.

---

## 17. Glossary

| Term | Meaning |
|---|---|
| **Attempt** | One PSP operation (authorize, sale, capture, void) with its own key and outcome. A payment can have many |
| **Chargeback** | A card payment reversed by the customer's bank after a dispute |
| **Checkpoint** | The point where PostgreSQL writes dirty pages to the main files and records how far recovery must replay |
| **Clearing account** | A ledger account that holds money temporarily during a flow and must return to zero |
| **`Committed` / `Proposed` / `Open`** | Design status: adopted / suggested but not adopted / not decided |
| **Fencing** | Preventing a demoted primary from continuing to accept writes, so two nodes cannot both be primary |
| **fsync** | The system call that forces data to durable storage |
| **Idempotency key** | A client-chosen name for one intended effect; repeats return the same answer |
| **Outbox** | Events written in the same database transaction as the state change, then published later |
| **PITR** | Point-in-time recovery: base backup plus archived WAL, replayed to a chosen moment |
| **PSP** | Payment service provider: the company or bank connection that moves card money |
| **Quorum** | The minimum number of standbys that must acknowledge before a commit returns |
| **Replication slot** | A server-side marker that keeps WAL until a consumer has it; can fill a disk if it stalls |
| **RPO / RTO** | Maximum acceptable data loss / maximum acceptable downtime |
| **Settlement line** | One row of a PSP settlement file, matched against a capture or refund |
| **SSI** | Serializable Snapshot Isolation, the mechanism behind PostgreSQL's `SERIALIZABLE` |
| **3DS / SCA** | 3-D Secure / Strong Customer Authentication: the customer proves the card is theirs |
| **`UNKNOWN` / `REVIEW`** | Outcome not established / a person must decide; neither moves money automatically |
| **WAL** | Write-ahead log: the append-only record of changes that makes commits durable and replicas possible |
| **WAL segment** | One physical WAL file, 16 MB by default |
| **CK / K / L / C / D** | Correctness check / crash test / load test / fault-injection test / decision record |
