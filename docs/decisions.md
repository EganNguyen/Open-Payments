# Decisions

The decisions that shape Open Payments, the assumptions behind them, and the questions still open.

Status: Draft · Last updated: 2026-11-05

All values are starting points until measured. Other docs state the resulting rule; this doc says why.

## Decision records

### D1

**Detect deadlocks before lock waits time out.** Status: `Committed`.

- **Decision:** Set `deadlock_timeout` to 100 ms and keep `lock_timeout` at 200 ms. Retry only `40001` and `40P01`. A `55P03` (lock timeout) returns 503 with `Retry-After`.
- **Why:** With PostgreSQL's default `deadlock_timeout` of 1 s, a 200 ms `lock_timeout` aborts every waiter before the deadlock check runs. Deadlocks would appear as lock timeouts, go unretried, and leave the deadlock counter near zero.
- **Consequence:** Deadlocks become visible and retried. Test C34 races capture, refund and reconciler on one payment.

### D2

**One write path for money state.** Status: `Committed`.

- **Decision:** A shared payment-transition module is the only code that writes money state. The Payments API, reconciler, timers and PSP callback receiver all call it. The callback receiver is hosted in the Payments API.
- **Why:** The earlier "only the Payments API writes" rule was untrue, because the reconciler and callbacks also apply state changes, and no service owned callbacks.
- **Consequence:** "Workers only read" gains named exceptions. The conditional-update, ledger and outbox rules hold on every path.

### D3

**Gate on completed payments/s.** Status: `Committed`.

- **Decision:** Ramp steps 9 and 10 and the final report are gated on 5,000 completed payments/s. Offered req/s is derived from the workload mix.
- **Why:** The goal is completed payments, but the old final gate was 5,000 req/s, of which only 60% were payments in the example mix.
- **Consequence:** With the example mix and auto-capture, the target needs about 8,300 req/s, or about 16,700 with separate capture requests, so capacity estimates rise. "Completed" is [defined](glossary.md#standard-names) as a final committed answer to an attempt's request, since [D16](#d16).

### D4

**15,000 req/s is an overload survival test.** Status: `Committed`.

- **Decision:** At 15,000 req/s the system sheds excess load, holds 5,000 completed payments/s and does not crash.
- **Why:** The old text claimed 3x sizing, yet 60% database CPU at 5,000 leaves about 1.7x, and the tests already expected shedding.
- **Consequence:** Lower cost, but 15,000/s is not a served-capacity promise.

### D5

**One PSP idempotency key per operation.** Status: `Committed`.

- **Decision:** Each authorize and capture is its own `payment_attempts` row, with an `operation` column and its own key. Each refund has its own `refunds` row and key. A unique index allows one live or successful capture per payment.
- **Why:** Sharing one key across authorize and capture could make a PSP reject the capture or treat it as a repeat. Separate keys alone would let two capture requests both reach the PSP, so the index keeps that from happening; test C36 covers it.
- **Consequence:** A small schema and adapter-signature change.

### D6

**State changes only after the PSP confirms.** Status: `Committed`.

- **Decision:** Unresolved operations are tracked on the attempt or refund row and resolved by the reconciler. Refunds reserve first through `refund_pending_minor`, with the limit `refunded + pending + amount <= captured`, then call the PSP, then confirm or release.
- **Why:** Capture and refund timeouts had no defined handling, and the refund's order of database update and PSP call was undefined.
- **Consequence:** Refund SQL and tests C3 and C6 change. A refund shows as pending until confirmed. Merchants get `payment.capture_failed` and `payment.refund_failed`, and detection covers attempts and refunds as well as payments.

### D7

**Quorum synchronous replication.** Status: `Committed`.

- **Decision:** Use two synchronous-capable standbys in different zones, with quorum `ANY 1`.
- **Why:** With one synchronous standby, losing its zone either blocks commits or gives up RPO 0.
- **Consequence:** One more database replica per cell, and possibly a slightly different commit latency.

### D8

**`merchant_id` on every tenant-owned table.** Status: `Committed`.

- **Decision:** Add it to `payment_attempts`, `outbox`, `ledger_transactions` and `ledger_entries`. Provider-scoped `psp_events` is excluded.
- **Why:** The old rule said every table, but 5 of the 8 tables with DDL lacked the column.
- **Consequence:** More storage. Row-level security can cover the ledger.

### D9

**Serialize balance checks per account.** Status: `Committed`.

- **Decision:** Take a per-account advisory transaction lock on refund and payout balance checks only, never on the create-payment path.
- **Why:** Two concurrent transactions could both read enough balance and both insert a reservation.
- **Consequence:** Refunds on one merchant account are serialized. The cost is measured in L1, and test C35 covers concurrent refunds across payments against a low balance.

### D10

**Per-cell services, with the vault as the stated exception.** Status: `Committed`.

- **Decision:** Fraud decision and PSP adapters run per cell. The vault stays shared to keep PCI scope small.
- **Why:** "Cells share nothing on the payment path" conflicted with shared synchronous services.
- **Consequence:** Vault availability is a stated cross-cell risk.

### D11

**Target .NET 10.** Status: `Committed`.

- **Decision:** Use .NET 10 (LTS). Microsoft's [support policy](https://dotnet.microsoft.com/en-US/platform/support/policy/dotnet-core) lists its end of support as 14 November 2028, and 10 November 2026 for .NET 8 and .NET 9 (page checked 2026-11-05). Recheck that each library supports the chosen version before adopting it.
- **Why .NET fits:**
  - Payments spend most of their time waiting on a PSP or database, and async I/O holds thousands of waiting requests cheaply.
  - Strong types narrow where money bugs can hide, such as mixed currencies or unhandled states.
  - The web server, rate limiting, resilience, telemetry and profiling tools are mature ([Libraries](engineering-standards.md#libraries)).
  - It has a large developer pool.
- **Trade-offs:**
  - GC pauses are possible.
  - One blocking call on a hot path can starve the ThreadPool.
  - EF Core can hide query cost.
  - Fewer ready-made payment engines exist in C# than in Java.
  - Several popular .NET libraries have moved to commercial licenses, so scan licenses before adopting anything (gate S6).
  - Memory per instance is higher than Go or Rust.
- **Revisit when:**
  - The team has strong experience in another stack that meets the same gates, and no C# experience.
  - The hot path cannot accept any garbage collection, which is unusual when PSP time dominates.
  - The project adopts a Rust or Java payment core.
- **How to test the choice:** Build the vertical slice and run the database spike first. Record the first bottleneck at each ramp step. Check Gen 2 rate, ThreadPool queue and allocation per request in L2 and L5. If CPU or GC is the first limit and cannot be tuned away, revisit that component only.

### D12

**Add a `REVIEW` state.** Status: `Committed`.

- **Decision:** `REVIEW` is a non-final state, entered on an amount or currency mismatch and left through manual resolution.
- **Why:** Failure handling put payments "in a review state" that the state machine did not have.
- **Consequence:** FS-07 and C32 use this state. A refund mismatch moves the refund row, not the payment, to `REVIEW`. Review tooling stays `Open`.

### D13

**Shed overload; do not queue it.** Status: `Committed`.

- **Decision:** Remove the critical tier's durable payment queue, the request dead-letter queue and limiter queueing from the deadlock proposals. Overload is shed with 503. Proposed tiers follow the [shed order](failure-scenarios.md#rules-for-every-failure).
- **Why:** These mechanisms conflicted with the no-queue rule and could accept a payment before its commit.
- **Consequence:** Fewer proposed mechanisms.

### D14

**Build one cell first.** Status: `Committed`.

- **Decision:** Start with one cell. Split only if gate G1 shows one primary cannot sustain the measured write rate at 60% CPU or less with synchronous replication on.
- **Why:** Cells add routing and operations cost, and need is unmeasured.
- **Consequence:** `merchant_id` is present from day one, so moving merchants later needs no schema change. Moving the data is still a substantial job ([Stripe research](stripe-research.md#5-moving-data-is-the-hard-part)).

### D15

**Split deployables by transaction and boundary, not by capability count.** Status: `Committed`.

- **Decision:** Keep the capability map separate from the deployable list. A capability becomes its own deployable only under the [split rule](service-catalog.md#when-to-split-a-service). Start with the nine deployables already designed.
- **Why:** Throughput doesn't set the service count. Splitting payment operations, idempotency or ledger writes would turn one database transaction into a distributed one, and would add a network hop to the payment path.
- **Consequence:** A full platform can have 40 or more capabilities without 40 or more services. Future deployables are added only when their capability enters scope. The capability map is our own proposal, not Stripe's internal design.

### D16

**A declined attempt returns the payment for another try.** Status: `Committed`.

- **Decision:** A decline is an attempt outcome, not a final payment state. The payment returns to awaiting a payment method and stays one payment per order. A unique index allows one live or successful authorize per payment.
- **Why:** With `DECLINED` final, a retry creates a second payment for the same order, which the idempotency key can't catch ([E5](failure-scenarios.md#edge-cases)). See [Stripe research](stripe-research.md#3-cards-are-the-special-case).
- **Consequence:** `DECLINED` is no longer a payment state. Payments end through the new `CANCELED`, by merchant request or when the window ends. Each attempt passes the fraud check, and attempts per payment are capped, because retries are also how card testing works. [D3](#d3)'s "completed" is now per attempt, so a decline still counts toward the 5,000 goal, and a retried payment counts once per attempt. The merchant event `payment.declined` becomes `payment.attempt_failed`. A late approval on a failed attempt goes to `REVIEW`. See the [lifecycle](payment-lifecycle.md#attempts).

### D17

**Design for customer action now; prove it with one 3D Secure flow.** Status: `Committed`.

- **Decision:** The state machine and adapter gain an "action required" result, and the adapter takes a payment method reference instead of a `CardToken`. One real 3D Secure flow runs against a PSP sandbox before the API contract is frozen. Full production rollout depends on target markets, which are `Open`.
- **Why:** The docs had no authentication path, and reshaping a public API later is costly. See [Stripe research](stripe-research.md#3-cards-are-the-special-case).
- **Consequence:** The API contract waits for that flow. Version 1 still ships cards first, but later payment methods add values rather than a new state machine. An action window that ends goes to `UNKNOWN`, never straight to failed, because the customer may have just finished. See [Customer action](payment-lifecycle.md#customer-action).

### D18

**Ingest settlement files in version 1, without payouts.** Status: `Committed`.

- **Decision:** The reconciler ingests PSP settlement lines and posts them through the payment-transition module, matching each to its payment. Payouts stay out of scope.
- **Why:** Daily totals can't show which payment failed to settle, and clearing checks need these lines to clear the PSP receivable. See [Stripe research](stripe-research.md#1-balanced-is-not-the-same-as-correct).
- **Consequence:** Parsing and matching work per PSP in Phase 2. Settlement is tracked per line and never changes the payment state, because refunds can still be running. `SETTLED` stays `Proposed`. Unmatched or mismatched lines raise a discrepancy and post nothing. Ledger entries use the accounts in [D21](#d21). If cells are adopted, a file spanning cells needs a routing rule. See [Settlement lines](payment-lifecycle.md#settlement-lines).

### D19

**Keep idempotency keys for at least 24 hours after their operation resolves.** Status: `Committed`.

- **Decision:** Publish 24 hours as the minimum retention. A key is never expired while its operation is unresolved, such as a payment in `UNKNOWN`.
- **Why:** [E4](failure-scenarios.md#edge-cases) promised a period but gave no value. Without the unresolved rule, a retry after expiry would look new and could charge twice. Extending retention later is easier than shortening it.
- **Consequence:** About 432 million rows per day of retention at target rate. How to expire them is measured in the database spike, because time partitions would break the unique key.

### D20

**Merchants get a test mode with separate keys and data.** Status: `Committed`.

- **Decision:** Test API keys reach isolated test data and a simulated PSP, with the same rate limits as live. Test traffic is shed first under load. Live money tables never hold test rows.
- **Why:** Merchants need a safe place to integrate, and adding test keys later touches authentication and routing. See [Stripe research](stripe-research.md#8-separate-rate-limits-from-load-shedding).
- **Consequence:** The API contract defines key types, and routing must consider the key's mode as well as the merchant. The isolation mechanism is `Open`; a dedicated test cell is one candidate.

### D21

**Specify accounts and fund flows before the first migration.** Status: `Committed`.

- **Decision:** Adopt the [fund flows](stripe-research.md#1-balanced-is-not-the-same-as-correct) from the Stripe research. Every account is `clearing` or `terminal`, and `real` or `logical`. Authorization entries are logical. A ledger transaction may not mix real and logical accounts, so a capture posts two ledger transactions in one database transaction: one releases the hold and one records the money, each with its own `source_event_id`. Ledger rows gain `effective_at` and `subject_id`. Add minimal `currencies` and `api_keys` tables.
- **Why:** The `accounts` table was named but unspecified, which blocked the first migration. Clearing accounts make a missing settlement visible, and keeping logical entries apart from real ones stops a hold from ever looking like cash.
- **Consequence:** A capture writes more rows. Fee and adjustment treatment stays `Open`, and benchmarks post up to 2 more entries per capture to cover it. The merchant platform stays out of scope, so `api_keys` is the only merchant record for now.

### D22

**Keep the money decision free of I/O.** Status: `Committed`.

- **Decision:** Split the payment-transition module into a pure `Decide` function, with no database, clock, random or network access, and a thin shell that loads a snapshot, calls `Decide` and writes the result in one transaction. Time and IDs are inputs. Amends [D2](#d2): the one write path is unchanged.
- **Why:** Exhaustive state tests and golden datasets become cheap, and old and new rules can be compared on recorded inputs. See [Stripe research](stripe-research.md#4-keep-the-money-decision-free-of-io).
- **Consequence:** Business rules may not live in the shell. A test enforces that the `Decide` project references no I/O.

### D23

**Add independent checks and a canonical log line.** Status: `Committed`.

- **Decision:** Add correctness checks CK7 to CK10 for clearing, timeliness, completeness and cross-checks, in code that doesn't reference the payment-transition module. Each request also emits one wide log line.
- **Why:** A balanced ledger can still be wrong, and the module can't check itself. The log line gives platform overhead as a direct measurement, total time minus PSP wait.
- **Consequence:** The checks run after every load and chaos test. Every check must fail on injected corruption and on zero rows.

### D24

**Correct the derived load and the phase gates.** Status: `Committed`.

- **Decision:** Count the real two-transaction flow in the derived load. Split C31 by phase. State which correctness checks apply at each stage. Add minimal synchronous fraud, the vault and the edge gateway to Phase 4, and run L5 again after Phase 5 as the sign-off number.
- **Why:** Phase 1 required edge cases that depend on later phases, gates required checks with no broker or reconciler to satisfy them, and the headline number would have come from a path missing a synchronous step.
- **Consequence:** Runs before Phase 3 are previews and prove nothing on the checklist. The estimated write load about doubles, which the database spike must test.

## Assumptions and limits

- **Numbers are targets, not measurements.** SLOs, thresholds, pool sizes and the 60% CPU target are replaced by measured values from L1 and O3.
- **The PSP must support idempotency keys and status inquiry by our ID.** The design depends on both.
- **The PSP contract is outside this design.** If the acquirer or PSP caps below the target, the answer is more PSPs or negotiation, not more code.
- **Synchronous replication costs latency.** If p99 is breached, tune commit batching or add cells. Moving to asynchronous replication gives up RPO 0, which is an owner decision, not a tuning step.
- **Exactly-once is not claimed.** Outcomes are effectively-once through idempotency keys and conditional transitions. Broker idempotence alone is not relied on.
- **The ledger balance trigger may cost too much.** The fallback is the continuous audit query plus property tests.
- **Single region.** Surviving a whole-region loss needs a cross-region replica or restore process, with RPO above 0. It is out of scope for the first version, and O5 covers backup restore only.
- **A mock PSP is not a real PSP.** Run against the real PSP sandbox before sign-off.
- **Not covered:** payouts and settlement beyond file ingestion ([D18](#d18)), Connect and KYC, billing, fraud machine learning, and the formal PCI DSS assessment. Keeping card data in the vault reduces PCI scope, but this is not a compliance assessment. Legal and banking work is also outside this design.
- **Database choice.** The SQL is PostgreSQL-specific. SQL Server could meet the same requirements with different syntax and settings.

## Open questions

- **Open-source parts.** Hyperswitch, Formance Ledger and TigerBeetle are candidates, none written in C#. Adopting one could change D11.
- **Data model gaps.** Inbox, balance snapshot, delivery log and discrepancy tables are named but not yet specified.
- **Manual review tooling** for `REVIEW` and stuck payments needs a simple internal screen.
- **Fraud fallback policy** needs a business owner.
- **Shed order** in [Rules for every failure](failure-scenarios.md#rules-for-every-failure) needs owner confirmation.
- **Repeated order references.** An optional warning for edge case E5 is not designed.
- **Settlement and refunds.** Refunds after `SETTLED` and disputes after a partial refund are undecided. See [lifecycle open questions](payment-lifecycle.md#open-questions).
- **Near-term documents.** An API contract, event catalog and acceptance tests are still to be written.
