# Decisions

The decisions that shape Open Payments, the assumptions behind them, and the questions still open.

Status: Draft · Last updated: 2026-11-10

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
- **Consequence:** The offered target depends on the share of two-call payments. It is 10,000 req/s at the mix set by [D47](#d47), from about 8,300 with no separate captures to about 16,700 when every payment has one. "Completed" is [defined](glossary.md#standard-names) as a final committed answer to an attempt's request, since [D16](#d16).

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

**Serialize balance checks per account.** Status: `Committed`, deferred by [D38](#d38) until fees or payouts exist.

- **Decision:** Take a per-account advisory transaction lock on refund and payout balance checks only, never on the create-payment path.
- **Why:** Two concurrent transactions could both read enough balance and both insert a reservation.
- **Consequence:** Refunds on one merchant account are serialized. The cost is measured in L1, and test C35 covers concurrent refunds across payments against a low balance.

### D10

**Per-cell services, with the vault as the stated exception.** Status: `Committed`.

- **Decision:** Fraud decision and PSP adapters run per cell. The vault stays shared to keep PCI scope small.
- **Why:** "Cells share nothing on the payment path" conflicted with shared synchronous services.
- **Consequence:** Vault availability is a stated cross-cell risk. [D39](#d39) defers the vault, so this applies only if one is built later.

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

- **Decision:** Count the real two-transaction flow in the derived load. Split C31 by phase. State which correctness checks apply at each stage. Add minimal synchronous fraud and the edge gateway to Phase 4, and run L5 again after Phase 5 as the sign-off number.
- **Why:** Phase 1 required edge cases that depend on later phases, gates required checks with no broker or reconciler to satisfy them, and the headline number would have come from a path missing a synchronous step.
- **Consequence:** Runs before Phase 3 are previews and prove nothing on the checklist. The estimated write load about doubles, which the database spike must test. [D29](#d29) and [D30](#d30) refine the numbers.

### D25

**Hash-partition the hot tables, and retire most data by batched delete.** Status: `Committed`.

- **Decision:** Partition 32 ways by hash: `payments` by `id`, `payment_attempts` by `payment_id`, `ledger_transactions` by `id`, and the outbox's current range by `id`. A ledger transaction's `id` must equal its `source_event_id`. `outbox_unpublished` joins the schema. `payment_attempts` and `settlement_lines` refer to an attempt by `(payment_id, attempt_id)`. Only the outbox stays range-partitioned, so old partitions can be dropped. Payments, attempts and the ledger retire by batched delete or archive. How long data is kept stays with the owner.
- **Why:** On the [MacBook spike](benchmarks/spike/macbook-2026-11-05/summary.md#durable-rate-search), this cut lock-timeout aborts from hundreds per rate to 0-8. Writers had been queuing to extend `payments`. It also gives each partition its own last pages, where writers contended. Time-ordered partitions (schema B) weren't measured.
- **Consequence:** `attempt_id` and `source_event_id` aren't unique alone in the database. A lookup by `attempt_id` or `psp_reference` alone probes all 32 partitions, so PSP metadata should carry `payment_id`. Merchant lists also probe all of them, at a read cost not yet measured. Violations name the partition's index, so the application matches the name's rule suffix, such as `_one_live_authorize`. The partition count needs a table rewrite to change. Schema C has one measured run, and V5 and V7 weren't run, so its benefits need repeating on Linux. A job that creates outbox partitions must exist before Stage 3. Until then, everything lands in the default partition.

### D26

**G1 stays open, and the build continues on one cell.** Status: `Committed`.

- **Decision:** A MacBook can't answer G1, so it's closed by a Linux run before Stage 4. Until then, the build targets one cell, per [D14](#d14).
- **Why:** The MacBook's limit is its one SSD, which all three database copies share, so its numbers don't describe a production primary. [The capacity assessment](benchmarks/spike/macbook-2026-11-05/summary.md#production-capacity-assessment) finds 5,000 payments/s plausible with cells and one primary not shown, and that isn't a measured answer.
- **Consequence:** The build now starts before G1, reversing the order the plan first set. If Linux requires cells, Stage 4 gets replanned. `merchant_id` on tenant tables keeps that open, but moving data and adding the router remain real work. G3, G4 and G6 stay open too.

### D27

**A negative answer from a PSP needs proof.** Status: `Committed`.

- **Decision:** An attempt becomes `FAILED` only on an explicit decline, a confirmed cancel, or a not-found from a lookup the adapter declares authoritative, after a quiet window. Anything else is inconclusive, and the attempt stays `UNKNOWN`. Each adapter declares how it recovers: authoritative lookup, replay with the same key, or technical cancel. A replay happens only well inside the PSP's key retention, and never after it. The first recovery action waits for the attempt timeout, which is longer than the PSP deadline.
- **Why:** The old rule, "PSP reports unknown attempt, so `FAILED`", can double-charge. A delayed request can land after the lookup, and the customer may then pay again on a new attempt. Real lookups also lag: [Stripe Search](https://docs.stripe.com/search) can be up to an hour behind in outages and isn't meant for read-after-write. Stripe's [guidance](https://docs.stripe.com/error-low-level) is to retry with the same key within 24 hours, and not to retry with any key after that.
- **Consequence:** `InquireAsync` becomes `RecoverAsync`, with an inconclusive result, and `UNKNOWN` can last longer. Replay can complete a charge the PSP never received, which is correct because the customer asked for it and hasn't been told it failed. The assumption that every PSP supports inquiry by our ID is withdrawn. Tests C44 and C45 cover this.

### D28

**Never cancel a commit, limit the wait, and treat any interruption as unknown.** Status: `Committed`, amended 2026-11-05 after [observing the behavior](../benchmarks/spike/commit-cancel.md), and again by the owner the same day after [probing Npgsql](../benchmarks/npgsql-commit-probe/README.md) (E1).

- **Decision:** No cancel request is ever sent during `COMMIT`. At its wait limit (starting at 3 s), the runner stops waiting for the commit and its outcome is unknown. The commit keeps running on its connection, which stays held until the commit finishes. Any failed commit or dropped connection is also unknown. A server warning during `COMMIT` that the wait was cancelled also makes the outcome unknown, and pages. After an unknown transaction A, the PSP is not called and the client gets 503. After an unknown transaction B, the client gets 202 and the attempt stays findable.
- **Why:** On PostgreSQL 18.6, directly and through PgBouncer, a cancel during the synchronous wait returned **a normal `COMMIT` success with a warning**, while the row was visible on the primary and on neither stalled standby. A failover at that moment would lose a commit the client was told about. `statement_timeout` doesn't limit this wait, so a stalled commit would otherwise hang for as long as both standbys are stalled, breaking the rule that every wait has a limit.
- **Consequence:** The transaction runner keeps commit out of the request's cancellation and owns the wait limit. If both standbys stall, each abandoned connection holds a pool slot until they recover, so the database concurrency limit sheds new work and writes stop until a standby returns. That is the correct price of RPO 0. Npgsql's `Command Timeout` covers `COMMIT` and sends a cancel when it fires, so the runner's connections set it to 0. Dropping the connection instead would free nothing on the database, because the backend keeps waiting after its client goes away. Test C43 covers the full path.

### D29

**Auto-capture is one PSP call where the PSP supports it.** Status: `Committed`.

- **Decision:** Add a `sale` operation and the transitions into `CAPTURED` that it needs. Adapters declare the capability. Where it's missing, auto-capture stays two calls. Sales post only the money entries, with no hold.
- **Why:** Two calls double PSP time and quota, double the commits, and add a state where authorization succeeded but capture failed, all to serve customers who never wanted the separation.
- **Consequence:** An auto-captured payment drops from up to 4 commits to 2, which G1 depends on. The one-live-attempt index covers `sale`. `AUTHORIZED` is skipped on this path, so the merchant gets `payment.captured` alone.

### D30

**Callbacks apply atomically, and count as load.** Status: `Committed`.

- **Decision:** The callback receiver inserts into `psp_events` and applies the event in one short transaction. If the event can't apply yet (E10), it commits with `processed_at` null, and a worker retries it. Adapters pass `attempt_id` to the PSP as the reference, so a callback can find its attempt before `psp_reference` is known. Callbacks are in the load model, with a concurrency limit of their own.
- **Why:** The old text could record an event as seen and then crash before applying it, and the dedupe would then drop the retry. Separately, each PSP operation can produce a callback, which adds thousands of requests and commits per second that no capacity figure included.
- **Consequence:** Callback traffic joins the mix and the mock PSP. `psp_events` needs a retention plan in D25.

### D31

**Idempotency answers are defined for every case.** Status: `Committed`.

- **Decision:** All mutating requests share `idempotency_keys`, so `refunds` loses its own key column and unique index. The request hash covers the method, route and body. A request refused before its work began stores nothing, so the same key can run later. Once an operation has started, a replay of a finished operation returns the stored answer, and a replay of an unresolved one renders the current state.
- **Why:** The docs said a retry "returns the stored answer", but an unresolved operation's stored 202 goes stale. Two key stores also cost a second unique index on the refund path.
- **Consequence:** A key reused across routes gets 422. E25 and E27 cover this.

### D32

**Event and ledger identity are deterministic.** Status: `Committed`.

- **Decision:** `payments.event_seq` rises by one for each outbox event, and that value is the event's `sequence`. `source_event_id` and `event_id` are derived from the attempt or refund ID and the posting kind. The balance check is per `txn_id` and currency.
- **Why:** Using `version` as the sequence would leave gaps, because a refund reservation bumps it with no event, and consumers park on gaps. Deterministic IDs make a repeated decision produce the same rows, which unique constraints then reject.
- **Consequence:** One more column on `payments`. `Decide` receives the IDs as inputs or derives them purely, as [D22](#d22) requires.

### D33

**Check the key before fraud, and record fraud blocks.** Status: `Committed`.

- **Decision:** A read of the idempotency key comes before the fraud check, and a replay skips fraud. A fraud block is an attempt outcome: `FAILED` with `failure_code = fraud_blocked`, committed with no PSP call, and answered with 402.
- **Why:** With fraud first, retries inflate velocity counters and spend the 50 ms budget on requests that do nothing. A block also had no row, no event and no answer defined anywhere.
- **Consequence:** One extra read on the primary per request. The unique index stays the real guard against duplicates, since the read can race.

### D34

**Separate limits for each kind of waiting, and fixed timer ordering.** Status: `Committed`.

- **Decision:** Requests, PSP calls per PSP, database work and callbacks each have their own concurrency limit. The reconciler has its own PSP quota, inside the PSP's limit, so recovery can't starve live traffic. Timers must keep this order: database call (200 ms) < PSP deadline (2.5 s) < total request budget (5 s) < attempt timeout (starting at 30 s) < PSP key retention.
- **Why:** One in-flight cap can't tell 5,000 requests politely waiting on a PSP from a stuck database. A PSP slowdown also creates `UNKNOWN` attempts in bulk, and their inquiries would compete for the quota live payments need.
- **Consequence:** More limits to tune in L1. Values remain starting points.

### D35

**Merchants connect their own PSP accounts, and v1 never holds funds.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** Each merchant has one or more `psp_accounts`. Money moves between the customer, the merchant's PSP account and the merchant's bank. Open Payments never holds it. Rejected: holding funds and paying merchants out, as a payment facilitator does.
- **Why:** Holding funds brings licensing, banking and payouts, which are out of scope. It also sets the PSP limit: Stripe's [default of 100 requests/s](https://docs.stripe.com/rate-limits) applies per account, so one platform account can't reach the target by default, while many merchant accounts might. [Hyperswitch](https://hyperswitch.io/blog/building-hyperswitch-the-world-s-first-open-source-payments-platform) is an open-source example of connecting merchants' own processors.
- **Consequence:**
  - Rate limits, callback secrets and settlement files are per PSP account, and credentials live in external secrets, never in the payment database.
  - The ledger is a record of what the merchant's PSP owes the merchant. `merchant_payable` and `settlement_bank` in [D21](#d21) are the merchant's position, not our liabilities.
  - One merchant's traffic is bound by its own account's limit, so G2 becomes a question per account.
  - A revoked credential fails that merchant only, which test C51 covers.
  - Legal advice is still needed, because this reduces regulatory exposure but doesn't remove it.

### D36

**Is 5,000 completed payments/s a peak or an average?** Status: `Proposed`, owner decision.

- **Recommendation:** A peak. Keep L5 at 5,000/s for 60 min. State an average rate for storage sizing, and run the L2 soak at that rate plus peak bursts.
- **Why:** 5,000/s for a full day is 432 million payments. Visa reports [257.5 billion processed transactions](https://s29.q4cdn.com/385744025/files/doc_downloads/2025/Visa-Fiscal-2025-Annual-Report.pdf) in fiscal 2025, about 705 million a day, so this would be about 61% of Visa's daily average. Data volume, backups and restore time on one primary may limit before throughput does.
- **Consequence if accepted:** L2 and the storage model change. The throughput target doesn't.

### D37

**Build void and authorization expiry in v1.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** `VOIDED` and `EXPIRED` are `Committed`. A void is its own attempt row and PSP key. One live or successful capture or void is allowed per payment, so the two can't race. A late-approved attempt is voided automatically where the PSP allows, through a void row that names its target attempt.
- **Why:** Without them an `AUTHORIZED` payment has no exit but capture. The merchant can't release a customer's funds, the `auth_hold` clearing account can't clear ([D21](#d21)), and CK7 would page on every uncaptured payment. A late approval (C38) also needs a person to reverse it, when a void can fix it automatically. Adyen's [authorizations usually last 28 days](https://docs.adyen.com/account/payments-lifecycle), so expiry is a real case.
- **Consequence:** A new adapter call, two new states in the contract, a per-PSP authorization window, and test C50. A late-approved sale can't be voided, because it is already captured, so it stays with a person. Late voids use a separate unique index, so one can't block the payment's own capture.

### D38

**Defer the per-account refund lock until balances can actually go negative.** Status: `Committed`, owner decision 2026-11-05. Amends [D9](#d9).

- **Decision:** v1 has no advisory lock and no balance check on refunds. The conditional update on the payment remains the limit. Both come back before any fees or payouts ship.
- **Why:** With no payouts and no retained fees, refunds on each payment can't exceed its capture (CK3), so the sum can't go negative. [D35](#d35) makes this firmer, because the platform pays no one out. The lock guards nothing yet, while it may cap one merchant at a few hundred refunds/s and add a lock to the deadlock order.
- **Consequence:** C35 and the balance part of U3 wait for that later work, and checklist row 87 is proven in v1 by U3 and C6. **Adding fees or payouts without restoring this is a defect,** because a full refund with a retained fee could then take a balance below zero.

### D39

**Use PSP-hosted card fields in v1, and defer our own vault.** Status: `Committed`, owner decision 2026-11-05, pending an assessor's confirmation. Amends [D10](#d10).

- **Decision:** Card data never reaches our systems. `method_ref` is a PSP token. Browsers talk to the API with a limited client secret, designed in Stage 4. The vault is deferred, leaving eight deployables.
- **Why:** [Hosted fields can keep merchants in the lightest assessment](https://www.securitymetrics.com/blog/ultimate-cheat-sheet-making-online-pci-compliance-work-you), while handling card data brings the full one. The vault was also the one shared dependency on the payment path. These are vendor sources only, so **a PCI assessor must confirm the scope before any live card traffic.** This doesn't block building.
- **Consequence:** PSP tokens don't move between PSPs, so failover (C13) only works for methods the second PSP can charge, and needs the merchant to hold a second account. Phase 4 drops the vault. A client-secret flow still needs designing, because the docs let customers start attempts while the API has only secret merchant keys.

### D40

**The console is a separate deployable that can't write money tables.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** `OpenPayments.Console` reads from a replica and moves money only through the API or the internal resolution route. It doesn't reference `OpenPayments.Transitions`.
- **Why:** Staff search must not slow live payments, and a second path that moves money would break [D2](#d2).
- **Consequence:** One more deployable. Staff can briefly see lag on the replica, so pages show a "data as of" time and read the primary after an action.

### D41

**`REVIEW` resolution requires two people and uses an internal route.** Status: `Committed`, owner decision 2026-11-05. Closes the open review tooling in [D12](#d12).

- **Decision:** One person records the PSP's answer, evidence and a proposed outcome. A different Approver approves it. `OpenPayments.Transitions` applies it in one transaction with its ledger, outbox and audit rows, and the database refuses self-approval.
- **Why:** The resolution decides where money ends up, and the state machine has no other check on it.
- **Consequence:** A business needs at least two money-role staff. Resolving also needs new domain commands, because refund confirm and release reject a refund in `REVIEW`, and the attempt must leave `REVIEW` with its payment.

### D42

**Audit uses intent and outcome rows for actions through the API.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** The console writes an intent, with its idempotency key, before calling the API, and an outcome afterwards. An intent with no outcome after a limit is flagged and checked by reading the payment.
- **Why:** The call crosses a service, so one transaction can't cover both. This mirrors writing the attempt before calling the PSP.
- **Consequence:** An intent can exist for an action that never ran, so an intent alone doesn't prove the action happened.

### D43

**Search is limited to a 31-day range in version 1.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** Lists use the existing `payments_merchant_created` index with a required date range. ID lookup has no limit. There are no broad search indexes and no search store.
- **Why:** Physical replicas share the primary's indexes, so a search index on a replica would still cost every payment write.
- **Consequence:** Finding an older payment needs its ID. New partial indexes for the review queues and `refunds_payment` are `MEASURE-FIRST`. [The comparison](../benchmarks/console-indexes.md) found no cost in its valid runs, and [D50](#d50) moves `refunds_payment` earlier.

### D44

**A reversal made at the PSP is recorded, not performed.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** A late-approved sale can't be voided, so a person reverses it at the PSP. Through the two-person flow, the attempt then becomes `FAILED` with `late_approval_reversed`, with evidence and no ledger entry, because none was posted for it.
- **Why:** The console must never claim to have moved money it didn't move, and the charge and its reversal net to zero.
- **Consequence:** Settlement files can still list both the charge and the reversal, with no attempt ledger to match. How these lines are matched is decided when settlement reconciliation is designed.

### D45

**Escalation past the age limit moves unresolved operations to `REVIEW`.** Status: `Committed`, owner decision 2026-11-05.

- **Decision:** An authorize or sale attempt in `UNKNOWN` past the age limit moves with its payment to `REVIEW`. A capture attempt moves with its `AUTHORIZED` payment to `REVIEW`. A refund moves to `REVIEW` on its own row, and its payment doesn't change. A void attempt isn't escalated in version 1.
- **Why:** The failure docs have a person resolve these, but the transitions had no manual way out of `UNKNOWN`. `REVIEW` already has one, with two-person approval.
- **Consequence:** While in `REVIEW`, a PSP answer doesn't move the payment, so a person sees and decides it. A resolved attempt that later lands as approved takes the late-approval path. The timer job is built in Stage 2, and a stuck void stays visible and unresolved.

### D46

**Callback and key-expiry edge cases belong to Phase 2.** Status: `Committed`, owner decision 2026-11-06.

- **Decision:** C31a becomes E1-E3, E5-E8, E11-E13, E16-E18, E21 and E25-E29. C31b becomes E4, E9, E10, E19, E20 and E22-E24.
- **Why:** E9 needs the callback receiver and E4 needs the expiry job, and both are Phase 2 work. Their tests can't exercise real behavior before then. See [E2 and E3](gates/stage-1-escalations.md#e2-task-16-late-declined-callback-has-no-receiver).
- **Consequence:** Stage 1 results don't include the cost of expiring keys, and `idempotency_keys` only grows during Stage 1 runs. `BASELINE.md` must say so. V7 must be measured at Stage 2's design gate.

### D47

**The benchmark mix has 20% two-call payments.** Status: `Committed`, owner decision 2026-11-06. The 20% is an assumption, to be replaced by real merchant data.

- **Decision:** 80% of payments are one-call sales and 20% are authorize, then a separate capture request. Across all requests: 40% sale, 10% authorize, 10% capture, 15% status, 10% refund, 10% retry and 5% other. The target `T` is 10,000 req/s. A capture does not count as a second completed payment.
- **Why:** With no separate captures, capture never runs under load, although it is a committed mode. At 50% or more, the expected load needs at least one more active cell, with nothing to justify it. No merchant data exists to choose a more exact share. See [E5](gates/stage-1-escalations.md#e5-task-17-workload-mix-has-no-agreed-saletwo-call-shares).
- **Consequence:** With `x` as the two-call share, `T = 8,333 × (1 + x)`, and commits per payment are `3.5 × (1 + x)` with callbacks at their upper limit, or `2.33 × (1 + x)` without callbacks. That gives 4.2 and 2.8 here, and 21,000 commits/s at target. The production estimate moves from 4 active cells to 5, plus the spare. `C` and `T` must come from the same mix, so changing the share means rerunning step 5. Auto-capture on a PSP with no sale call takes 4 commits and adds no requests. It stays out of this mix and gets its own run when the first such PSP is added.

### D48

**S3 checks query growth and measured ceilings.** Status: `Committed`, owner decision 2026-11-06.

- **Decision:** S3 has three query rules. An endpoint's command count must not grow with related rows. Each path has a ceiling set to its measured count. Statements and round trips are recorded per transaction, with ceilings set from those measurements. Lowering a ceiling needs no approval, and raising one needs a design review. These per-transaction ceilings replace the plan's "at most about 5 statements per transaction" for Stage 1.
- **Why:** The standards require tests that catch N+1 patterns, and a request on one payment with no related rows can't show one. The builder's limits had no design source. Reading the code suggests the apply transaction runs about 12-14 commands, because each ledger line is its own command, so a limit of about 5 would force a rewrite before there is evidence it limits capacity. See [E6](gates/stage-1-escalations.md#e6-s3-query-count-budgets-have-no-design-source).
- **Consequence:** S3 stays PARTIAL until the test meets these rules. The statement count is a likely bottleneck, to be revisited after Linux step 5. If pool wait or lock waits limit capacity, batching is the first fix to try.

### D49

**Every stage has a frontend task.** Status: `Committed`, owner decision 2026-11-06. The screens added after Stage 2 still need their stage's design gate.

- **Decision:** Stage C stops being a separate stage. Its tasks move into stages 1 and 2, and stages 3, 4 and 5 each get a new frontend task. The split is in the [roadmap](superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md#frontend-tasks-by-stage).
- **Why:** The owner asked for a frontend task in every stage. The split follows what each stage provides: Stage 1's routes are enough for search, payment detail and money actions, and Stage 2 already lists review tooling, which is what the console's resolution tasks build.
- **Consequence:** Stage 1 can't reach its review gate until Task 18 is done, so its review now also covers the console's first screens. Stage 2's review covers the change to `Decide` in Task C3. Until Stage 2, there's no Needs attention screen and `REVIEW` can't be resolved from the console. F4's reference checkout is new scope, and no document designs it yet.

### D50

**`refunds (payment_id)` is indexed before the laptop limit search.** Status: `Committed`, owner decision 2026-11-06. Amends [D43](#d43) for this index.

- **Decision:** `refunds_payment` ships in its own migration, ahead of Task 17's limit search, instead of in Task C4. The three `REVIEW` indexes stay in Task C4.
- **Why:** `Sql.LoadRefund` filters on `payment_id`, which has no index, so a live refund lookup scans the whole table. On the laptop, `refunds` had about 19.7 billion rows read by sequential scans at 41,847 rows. Left alone, the limit search could report this as a bottleneck and hide the next real limit. See [the comparison](../benchmarks/console-indexes.md).
- **Consequence:** Every refund insert and update adds an index entry. The laptop comparison found no cost in its valid runs, but it is a preview. This index also serves the foreign key check when payments are retired.

### D51

**The OP-BENCH suite judges a laptop milestone of 500 completed payments/s, as a preview.** Status: `Committed`, owner decision 2026-11-09.

- **Decision:** The [benchmark suite](superpowers/specs/2026-11-09-benchmark-suite-design.md) adds an orchestrator, a fault driver, CK11 and a reliability test project around the existing harness. Its laptop milestone is 500 completed payments/s at ramp step 6 (about 1,000 req/s at the D47 mix), held for a 10-minute window in each of three runs from a reset. The project target stays 5,000. Latency is gated on measured platform overhead, p50 ≤ 25 ms and p99 ≤ 100 ms, using each request's own PSP wait. Recovery is first success within 30 s and SLO within 2 min of the fault's removal, with times from injection reported beside them. The k6 harness and the D47 mix are invoked unchanged. A laptop result is `PREVIEW`; a gate that is `NOT_APPLICABLE` makes the suite `INCONCLUSIVE`.
- **Why:** The proposal it replaces gated on end-to-end latency the 200 ms mock makes impossible, left out the PSP call between the two transactions, and replaced the D47 mix, which would break comparison with every earlier run. A laptop shares one SSD and 18 cores between the generator and three PostgreSQL instances, so it can't give a recorded capacity number.
- **Consequence:** New IDs TX1, TX2, C52-C55 and CK11 in [Verification](verification.md). `pg_stat_statements` and `log_lock_waits` join the database settings. Every suite writes a reproducibility manifest, and an incomplete one makes the result `INCONCLUSIVE`. Until Stage 2's resolver exists, the full suite's honest result is `INCONCLUSIVE (PREVIEW)`.

### D52

**CK11 reconciles what the client saw against the payment state machine.** Status: `Committed`, owner decision 2026-11-09.

- **Decision:** CK11 reads the fault driver's ack log, the database and the mock's effect log. Every acknowledged final answer has its payment, with the same ID and a state that answer allows. Every 202 or `UNKNOWN` answer has an unresolved or legally resolved attempt. Every unanswered key is classified into one of the PSP lifecycle cases L0-L5. Every replay returned the original payment ID. Every unresolved attempt is listed with its case. A client-observed failure may have a row, such as a decline or a commit-unknown 503 after transaction A, and CK11 checks the row's state, not its absence. PSP side effects stay in CK2, which already proves one effect per key and records both ways.
- **Why:** The proposal's CK11 compared status codes with row counts, which would flag legal `FAILED` rows and miss illegal states. Duplicating CK2's rules would give two checks that can disagree about one fact.
- **Consequence:** Fault scenarios require CK2 and CK11 together. Before Stage 2, CK11 lists unknown outcomes and resolution is `NOT_APPLICABLE`; it never claims they were reconciled. A lifecycle case with no classified payment is `INCONCLUSIVE` for its scenario.

### D53

**Stage 1's Task 17 closes on laptop evidence.** Status: `Committed`, owner decision 2026-11-09. Answers [E4](gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) for Stage 1.

- **Decision:** Task 17 is complete when the laptop limit search has run until its first failed gate, and that limit is recorded with its numbers. `C`, `N`, the mock PSP's 25,000 req/s check and step 6's scaling past one API instance move to the first Linux run, before Stage 4's load tests. *Amended by the owner on 2026-11-09:* the first version required valid runs through step 6, but step 6 fails its p99 gate on the laptop with one API instance.
- **Why:** No Linux host exists, and waiting for one blocks every later stage. A laptop run can't give `C`, so none is claimed.
- **Consequence:** Stage 1 reports no capacity number. The first Linux run is a required step in Stage 4's plan, and E4 stays open there.

### D54

**`TxRunner` uses the role's `lock_timeout`.** Status: `Committed`, owner decision 2026-11-09. Answers [E9](gates/stage-1-escalations.md#e9-txrunner-overrides-the-d1-lock_timeout).

- **Decision:** remove `set_config('lock_timeout', '2s', true)` from `TxRunner`, so [D1](#d1)'s 200 ms role setting applies. A test reads `lock_timeout` inside a runner transaction.
- **Why:** The override made lock waits run 10 times longer than D1 allows, and the settings check couldn't see it.
- **Consequence:** under a hot row, `55P03` returns 503 after 200 ms rather than 2 s.

### D55

**The console accepts MFA shown by `acr` as well as `amr`.** Status: `Committed`, owner decision 2026-11-09. Answers [E10](gates/stage-1-escalations.md#e10-local-keycloak-sign-in-carries-no-mfa-claim-so-the-console-refuses-it).

- **Decision:** a session passes the MFA rule if `amr` contains `RequiredAmr`, or if `RequiredAcr` is configured and `acr` equals it. The local Keycloak realm uses a level-of-authentication browser flow: password is level 1, TOTP is level 2, and the client maps `mfa` to level 2 and requests it by default.
- **Why:** Providers report MFA differently. Keycloak 26.0 can't put `otp` in `amr`, but it reliably reports a step-up level in `acr`.
- **Consequence:** each deployment configures the claim its provider sends. An empty `RequiredAcr` keeps the `amr`-only rule.

### D56

**Stage 1's local verification uses laptop-scaled criteria, recorded as previews.** Status: `Committed`, owner decision 2026-11-10.

- **Decision:** to get the strongest Stage 1 evidence a laptop can give, these run on the local stack, with these pass conditions fixed before any run:
  - **Capacity:** step 4 once and step 6 three times, each from a reset, with two API instances behind a local nginx, which is step 6's purpose. The stable rate is the highest step with valid runs whose gates all pass.
  - **Soak:** 60 minutes at the stable rate. It passes if the gates hold, CK1-CK6 pass, and memory, Gen 2 GC, ThreadPool queue and pool wait show no upward trend.
  - **L3, scaled:** offered load at 3 times the stable rate for 10 minutes. It passes if the excess is rejected with 429 or 503, there's no restart and no out-of-memory error, platform p99 is back under 100 ms within 2 minutes of the burst ending, and CK1-CK6 pass.
  - **C1, scaled:** the primary's CPU throttled to slow it about 5 times, under the stable load. It passes if requests are shed fast as 503s, there's no retry storm (`40P01`/`40001` retries under 1% of requests) and no ThreadPool starvation, and CK1-CK6 pass after.
  - **C2:** PgBouncer's pool shrunk below demand under load, then restored. It passes if waits stay bounded by `query_wait_timeout` and fail as 503s, and service recovers with no restart.
  - **C7, partial:** the primary killed under load, and the standby with the higher replay position promoted by hand. It passes on RPO 0 only: every payment the API answered 200 or 202 is present on the new primary, and CK1 and CK3 pass. Failover time isn't judged, because Stage 1 has no automated failover.
  - **Alerts:** each of the three rules is seen firing in a running Prometheus.
- **Why:** The owner asked for the highest defensible Stage 1 score from laptop evidence, without claiming production readiness or distributed validation. The verification table's criteria are written for 5,000 payments/s and real infrastructure.
- **Consequence:** every result is a preview. None of these replaces L3, L5, C1, C2 or C7 on Linux, and C10 and C11 aren't run.

### D57

**Database settings come only from reviewed config files, and the WAL settings suit production.** Status: `Committed`, 2026-11-10.

- **Decision:** `primary.conf` sets `wal_buffers = 64MB`, `wal_compression = lz4`, `autovacuum = on` and I/O timing (`track_io_timing`, `track_wal_io_timing`). Per-machine files such as `primary.macbook.conf` hold sizing only. `scripts/check-db-settings.sh` fails if any setting comes from `postgresql.auto.conf`, so an `ALTER SYSTEM` can't change behaviour unseen.
- **Why:** the 60-minute soak (D56) found the local primary running with `autovacuum = off` and `checkpoint_timeout = 1h` from a forgotten `ALTER SYSTEM`, in no file in the repo. The 16 MB `wal_buffers` default had filled 108,801 times under the D47 mix. These are general PostgreSQL practice, not laptop tuning. Asynchronous replication, `synchronous_commit` below `on`, `fsync = off` and RAM disks were rejected: each passes a test by giving up D7.
- **Consequence:** watch `pg_stat_wal.wal_buffers_full` and checkpoint timing on every server. Raising `wal_buffers` didn't remove the soak's commit stalls; those came from the shared virtual disk (D58).

### D58

**A key's idempotent answer commits in transaction B, with the result it describes.** Status: `Committed`, 2026-11-10.

- **Decision:** after the PSP answers, `PaymentStore.ApplyAsync` takes a `KeyAnswer` and writes the key's response in the same transaction as the payment, attempt, ledger and outbox changes. Used by sale, authorize, capture, void and refund. An unknown outcome (202) still leaves the key unresolved (D19). Cancel and payment creation without a method still resolve the key separately.
- **Why:** each auto-capture payment made three synchronous commits, and each waits for a standby (D7). On the laptop, the primary and both standbys share one virtual disk taking about 2,500 flushes per second, and the soak's p99 failures were `COMMIT`s queued behind short disk stalls. One fewer commit per payment removes a standby round trip on any hardware. It also closes a gap: a payment could be final with its key unresolved, if the API died between the two commits.
- **Consequence:** after a crash during transaction B's commit, a same-key retry replays the stored final answer (200) instead of rendering the state (202). The K2 crash test and `D58KeyAnswerTests` assert this.

### D59

**A PSP answer is never dropped, and callbacks share its table.** Status: `Proposed`, at Stage 2's [design gate](gates/stage-2-design.md), for the owner. Settles review finding [L7](gates/stage-1-review.md#l7-a-refused-or-lost-transaction-b-drops-the-psps-answer).

- **Decision:** When transaction B loses its conditional update or `Decide` refuses it, the store re-reads and runs `Decide` once more. If it is refused again, the PSP's answer is inserted into `psp_events` as a parked event with `source = 'api_answer'`, in its own short transaction, and the client gets 202 with the current state. If that insert fails too, the answer is still 202, and recovery reads the answer back from the PSP. Callbacks insert into the same table and apply in the same transaction ([D30](#d30)). An event that can't apply yet is parked and retried by a worker, and past its limit it is left to the reconciler with an alert.
- **Why:** Stage 1's review found the answer dropped with a 409 or a stale 200. With callbacks, the reconciler and timers, a race with transaction B becomes normal, and a refusal after the PSP acted tells the merchant something false.
- **Consequence:** `psp_events` gains a link to the payment and operation, a source, an outcome and retry columns, and becomes hash-partitioned. The parked-event worker must exist before callbacks ship.

### D60

**An adapter declares recovery per situation.** Status: `Proposed`, at Stage 2's design gate, for the owner. Amends [D27](#d27).

- **Decision:** `PspCapabilities` names three things: how to recover an operation the PSP never answered, with no PSP reference (replay, authoritative lookup or technical cancel); whether a lookup by the PSP's own reference is consistent, for when one is held; and how to prove an abandoned customer action can't complete (a confirmed cancel, or none). The reconciler chooses by what the attempt holds. A not-found is still proof only from a lookup the adapter declares authoritative, after its quiet window.
- **Why:** One mode per adapter doesn't fit Stripe. Its search lags and isn't for read-after-write, so with no reference only a replay is safe. Once it has answered with its own ID, retrieving by that ID is direct. An abandoned 3D Secure attempt is proven not charged only by cancelling it ([E20](failure-scenarios.md#edge-cases)).
- **Consequence:** More capability fields, each covered by U5's recorded fixtures. The mock declares each combination by configuration.

### D61

**Resolving an operation resolves its key, in the store.** Status: `Proposed`, at Stage 2's design gate, for the owner. Answers [E3](gates/stage-1-escalations.md#e3-task-16-idempotency-key-retention-has-no-implementation)'s second check.

- **Decision:** `payment_attempts` and `refunds` carry the idempotency key that started them, and a flag saying whether finishing them resolves it. Inside the transaction that moves an attempt or refund to `SUCCEEDED` or `FAILED`, the store sets the key's answer and `resolved_at`, by primary key, whoever applied it. The answer comes from one renderer in `OpenPayments.Transitions`, shared with the API. The authorize half of a two-call auto-capture doesn't resolve its key; the capture does.
- **Why:** [D58](#d58) resolves keys only on the request path. A key whose operation the reconciler or a callback finished would stay unresolved forever, and the existing off-request update finds keys by `attempt_id`, which no index covers.
- **Consequence:** Two columns on hot tables, written in transaction A, and no new index. A key whose capture never starts stays unresolved, so it is never expired.

### D62

**The reconciler claims work by lease, inside its own PSP quota.** Status: `Proposed`, at Stage 2's design gate, for the owner.

- **Decision:** `OpenPayments.Reconciler` is its own deployable and also runs the timers and the parked-event worker. It claims due attempts and refunds with `for update skip locked` in a short transaction that sets `next_recovery_at` to a lease and commits. It calls the PSP outside any transaction, applies the answer through the normal conditional update, and records the last inquiry on the row. Recovery spends a token bucket per PSP account set to a share of that account's limit, 10% to start, and live traffic keeps the rest ([D34](#d34)). Replay stops at a replay window of at most a quarter of key retention, and escalation follows at its own age limit ([D45](#d45)).
- **Why:** Holding a row lock across a PSP call breaks the rule of no network inside a transaction. Leases let two reconcilers run safely (C26), and the per-account quota keeps a backlog from starving live payments (C47).
- **Consequence:** Recovery columns on `payment_attempts` and `refunds`. A lapsed lease can repeat a PSP call under the same key, which the PSP dedupes.

### D63

**Settlement is ingested per account and per file, resumably.** Status: `Proposed`, at Stage 2's design gate, for the owner. Details [D18](#d18).

- **Decision:** A `settlement_files` row tracks each file per PSP account and its progress. Lines insert with `ON CONFLICT DO NOTHING`, then match by PSP reference. A matched capture, sale or refund line posts through `OpenPayments.Transitions`, with a ledger transaction ID derived from `(psp_account_id, file_id, line_no)` ([D32](#d32)). `UNMATCHED` and `MISMATCH` lines are discrepancies that page, and an operation with no line past the adapter's settlement lag gets a `settlement_missing` row. Fee and adjustment lines are stored and post nothing.
- **Why:** C40 requires duplicate, unmatched, wrong and missing lines to end in a defined state, ledger entries to post once, and a killed ingest to resume.
- **Consequence:** Two new tables. CK8 timeliness applies from Stage 2.

## Assumptions and limits

- **Numbers are targets, not measurements.** SLOs, thresholds, pool sizes and the 60% CPU target are replaced by measured values from L1 and O3.
- **The PSP must support idempotency keys, and at least one recovery mode** ([D27](#d27)). The design depends on both. Inquiry by our ID is no longer assumed.
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
- **Fraud fallback policy** needs a business owner.
- **Shed order** in [Rules for every failure](failure-scenarios.md#rules-for-every-failure) needs owner confirmation.
- **Repeated order references.** An optional warning for edge case E5 is not designed.
- **Settlement and refunds.** Refunds after `SETTLED` and disputes after a partial refund are undecided. See [lifecycle open questions](payment-lifecycle.md#open-questions).
- **Near-term documents.** The [API contract](../api/openapi.yaml) and [event catalog](event-catalog.md) are drafts. The contract freezes after C39. Acceptance tests are written with the code.
