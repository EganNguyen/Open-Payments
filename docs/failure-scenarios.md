# Failure Scenarios

What can go wrong in the payment system, what it does by itself, how people recover it, and which test proves it.

Status: Draft · Last updated: 2026-11-05

**Nothing here is verified yet.** Every row is a design claim until its test has passed with a recorded run. Test IDs are defined in [Verification](verification.md), and thresholds and times are starting values to be replaced by measurements.

## Rules for every failure

These rules decide what the system protects first. Every scenario below is a case of one of them.

1. **Money is never lost or doubled.** If the system is unsure, it says "unknown" and finds out later. It never guesses.
2. **Save intent before acting.** The attempt is written to the database before the PSP is called ([Payment flow](architecture.md#payment-flow)), so a crash always leaves something the reconciler can find.
3. **Say "busy" quickly.** An overloaded service rejects extra work at once (429 or 503 with `Retry-After`). It does not queue it without limit.
4. **Every wait has a limit.** Every call to a PSP, database, broker or other service has a deadline.
5. **Every retry is safe.** Retries use the same idempotency key, and consumers ignore events they have already handled.
6. **Failure stays small.** A slow merchant, PSP or consumer must not slow everyone else (bulkheads, per-merchant limits, separate queues).
7. **Recovery is gradual.** After an outage, load and backlogs return in steps, not all at once.

**Order of protection when resources run short (`Proposed`, owner to confirm):**

| Priority | Protect | Shed first when overloaded |
|---|---|---|
| 1 | Correctness: no duplicate charge, no lost record, balanced ledger | never shed |
| 2 | Create-payment and its result | |
| 3 | Refunds and captures | |
| 4 | Webhook delivery (slow down, do not drop) | shed or slow earlier |
| 5 | Reports, exports and dashboards | shed first |

## Failure catalogue

Columns: **Detect** is the signal that shows it. **Automatic** is what the system does without a person. **Recovery** is what restores normal service. **Proof** is the test.

### Payment provider (PSP) and other outside services

| ID | Failure | Detect | Automatic | Recovery | Proof |
|---|---|---|---|---|---|
| FS-01 | PSP approves, but our call times out | PSP timeout counter; payments in `UNKNOWN` | On authorize the payment becomes `UNKNOWN`; on capture or refund its state is unchanged and the operation stays unresolved. API answers 202; no retry with a new attempt | Reconciler asks the PSP by `attempt_id` and applies the real answer; webhook goes out | C3, CK2, CK5 |
| FS-02 | PSP responds slowly (for example 2 s instead of 200 ms) | PSP latency p99; in-flight count rising | In-flight work rises (about 10,000 at 5k TPS and 2 s). Limiter sheds extra requests; deadline turns slow calls into `UNKNOWN`; breaker may open | Wait for the PSP, or fail over; reconciler clears `UNKNOWN`; raise instance count only if memory allows | C23, L6 |
| FS-03 | PSP is down | Error rate; breaker state open | Breaker opens; fail over to a second PSP where the contract allows, otherwise fast "unavailable" | Breaker half-opens with a few probe calls and closes when they succeed; reconcile payments that were in flight | C13 |
| FS-04 | PSP rate-limits us (429) | 429 count from PSP | Client-side limiter and backoff with jitter; no new attempt IDs | Lower send rate; ask the PSP for a higher limit | C12 |
| FS-05 | Duplicate, late or out-of-order PSP callbacks | Ignored-event counter; alert on a late event contradicting state | Conditional state update; stale events logged and ignored | Review the alert; no repair needed if state is correct | C9, U2 |
| FS-06 | PSP DNS fails or its TLS certificate expires | Connect and TLS error alerts | Breaker opens; fast failures | Fix DNS or certificate; breaker recovers; reconcile | C16 |
| FS-07 | PSP result disagrees with ours (amount or currency) | Amount-mismatch check on every PSP answer | Payment moves to `REVIEW`; no ledger entry for the disputed amount; alert | Person compares the PSP record, then corrects with a reversing entry | C32 |
| FS-08 | Fraud service is unavailable | Fraud timeout rate | Fallback policy per risk tier (for example allow low value, hold high value); each decision logged | Restore service; review payments decided by fallback | C14 |
| FS-44 | PSP lookup says "not found", but the request lands later | Late-approval alert; lookup age | The attempt stays `UNKNOWN` without proof, so no new attempt can charge. A late approval goes to `REVIEW` | Resolve through the adapter's recovery mode ([D27](decisions.md#d27)) | C44, CK2 |
| FS-45 | Attempt is still unresolved when the PSP's idempotency key expires | Unresolved age against key retention | Replay stops well before retention ends, so a new charge can't run | Escalate; a person compares the PSP's record | C45 |
| FS-46 | Crash during callback handling, or a callback arrives before transaction B | Parked event count and age | Insert and apply share one transaction; an early event parks and retries ([D30](decisions.md#d30)) | Worker clears parked events; alert past the limit | C46, CK5 |
| FS-47 | PSP slowdown creates thousands of `UNKNOWN` attempts | `UNKNOWN` count; recovery quota use | Recovery stays inside its own quota; live traffic keeps its share ([D34](decisions.md#d34)) | Backlog drains with a rate cap after the PSP recovers | C47, C23 |
| FS-48 | Callback flood from a PSP | Callback rate; rejected callbacks | Callbacks have their own concurrency limit; the PSP's retries deliver what was rejected | Raise the limit if sustained; reconcile anything late | C48 |

### Application and platform

| ID | Failure | Detect | Automatic | Recovery | Proof |
|---|---|---|---|---|---|
| FS-09 | API process killed at any point in a payment | Pod restart count; payments stuck in `PSP_PENDING` | [Crash recovery](architecture.md#crash-recovery) applies: nothing, a pending attempt, or a final state | Reconciler resolves pending attempts; client retry returns the stored result | C4, C15 |
| FS-10 | Deploy or scale-down (SIGTERM) | Release dashboards | Stop accepting, finish in-flight work within the grace period, drain the outbox | Roll back if errors rise | C20 |
| FS-11 | Bad release | Canary SLO breach | Promotion stops; automatic rollback | Fix forward after a postmortem; expand/contract migrations keep rollback safe | C19, S4 |
| FS-12 | Overload survival load (15,000 req/s) or stress | Rejected rate; in-flight count; CPU | Limiter rejects the excess with 429 or 503; no restart | Autoscaler adds replicas up to the connection-budget cap; rejected traffic retries later | L3, L6 |
| FS-13 | One merchant sends a large share of traffic | Per-merchant rate and concurrency | Per-merchant limits and bulkhead; other merchants keep their SLO | Contact merchant; move to its own cell if permanent | C30 |
| FS-14 | Memory growth, GC pauses, ThreadPool starvation | Gen 2 GC rate, ThreadPool queue, memory trend | Bounded queues; no blocking calls (build gate); pod restart as last resort | Take a dump and trace (runbook), fix, redeploy | L2, O2, S1, S2 |
| FS-15 | Clock skew on a node (for example 5 min) | NTP offset alert | Webhook signing window and token expiry use tolerance; ordering uses database time, not node time | Fix time sync; drain the node | C25 |
| FS-16 | Cache or Redis is unavailable | Cache error rate | Rate limits fall back to in-process limits; idempotency is unaffected because the database enforces it | Restore cache; warm gradually | C24 |
| FS-17 | Node or availability zone lost | Node and zone health | Remaining replicas serve; spread constraints keep replicas in several zones | Replace capacity; check headroom (40%) held | C10, C11 |
| FS-18 | Restart after a full outage (thundering herd) | Request spike and backlog right after recovery | Staged ramp; limiter; consumers resume at a capped rate | Follow runbook R7 | C28 |
| FS-19 | Cell router or its config store is unavailable (only if cells are adopted) | Routing error rate | Routers keep the last known mapping in memory; mapping changes are frozen | Restore the config store; verify mapping | C33 |
| FS-49 | Status-query flood | Read rate by merchant; shed count | Reads shed before writes; create-payment keeps reserved capacity | Contact the merchant; tighten its read limit | C49 |

### Database

| ID | Failure | Detect | Automatic | Recovery | Proof |
|---|---|---|---|---|---|
| FS-20 | Database is much slower | Query latency; pool wait | Statement timeouts; limiter sheds; no retry storm | Find the cause (locks, vacuum, plan change); scale or fix | C1 |
| FS-21 | Connection pool exhausted | Pool wait time; pool use | Bounded wait, then fast failure; connection budget prevents the autoscaler from making it worse | Fix the leak or reduce replicas; no restart needed | C2 |
| FS-22 | Primary fails | Health checks; failed writes | Automatic promotion of a synchronous standby; committed payments are present (RPO 0) | Applications reconnect; check CK1-CK6; rebuild a new standby | C7 |
| FS-23 | Replica lag causes a stale read | Replica lag metric | Reads that must be fresh (payment status right after create) go to the primary | Fix lag cause; route all reads to the primary if needed | C29 |
| FS-24 | Disk fills (database, broker, logs) | Alerts at 70% and 85% | Alerts only; no automatic delete of data | Expand volume, drop old partitions, or archive per runbook | C22 |
| FS-25 | Migration locks a table or breaks the app | `lock_timeout` errors; error spike | `lock_timeout` aborts the migration; expand/contract keeps old code working | Revert the migration step; retry off-peak | S4 |
| FS-26 | Data loss, corruption or a bad manual change | Continuous ledger audit; restore drill | Point-in-time recovery is available | Restore to a new instance, compare, then promote or repair with reversing entries | O5 |
| FS-42 | Deadlocks or lock contention on the same rows (for example capture, refund and reconciler updating one payment) | `pg_stat_database.deadlocks`; lock-wait counter; `40P01` and `55P03` error rate; retry budget use | `deadlock_timeout` 100 ms finds the cycle and aborts one victim; `40P01` is retried at most twice with jitter inside a retry budget; `lock_timeout` 200 ms turns other slow waits into `55P03`, which returns 503 with `Retry-After`; limiter sheds if retries pile up | Read the deadlock log, find the two statements, fix lock order or split the hot row; see [Deadlock handling](deadlock-handling.md) | L1, C6, C34 |
| FS-43 | Commit outcome is ambiguous: connection lost during `COMMIT`, or a cancel while waiting for a standby | Commit error count; PostgreSQL's "committed locally" warning | Commits are never cancelled. After a failed A, no PSP call. After a failed B, the answer is 202 ([D28](decisions.md#d28)) | Read the row; the reconciler completes anything unresolved. Any warning pages, because RPO 0 may be broken for that transaction | C43, CK2 |

### Messaging and webhooks

| ID | Failure | Detect | Automatic | Recovery | Proof |
|---|---|---|---|---|---|
| FS-27 | Broker is down or a broker is lost | Publish errors; outbox age | Outbox rows stay in the database; publisher retries | Backlog drains after recovery at a capped rate | C8, CK4, CK6 |
| FS-28 | Consumer crashes mid-batch or a rebalance happens | Consumer lag | Offsets commit after work; inbox table drops duplicates | Restart; lag drains | C17 |
| FS-29 | Poison message | Repeated failure count; DLQ depth | Limited retries, then dead-letter; the partition keeps moving | Fix the cause, then replay from the DLQ | C18 |
| FS-30 | Merchant webhook endpoint is slow or down for a long time | Delivery failure rate per merchant | Exponential backoff with jitter; per-merchant queue and concurrency limit; others unaffected | Merchant fixes endpoint; resend from the dashboard; events are kept for the retention period | C27 |
| FS-31 | Outbox or consumer backlog keeps growing | Outbox age; lag trend | Alert; publishers scale up to a cap | Find the slow stage (broker, consumer, downstream) | CK6, L5 |

### Money correctness

| ID | Failure | Detect | Automatic | Recovery | Proof |
|---|---|---|---|---|---|
| FS-32 | Same idempotency key sent many times at once | Duplicate-key counter | One payment and one charge; all callers get the same stored response | None needed | C5, U4 |
| FS-33 | Concurrent refunds above the captured amount | Rejected-refund counter | Atomic conditional update on the refunded and pending total | None needed | C6, CK3 |
| FS-34 | Ledger imbalance found | Continuous audit query; CK1 | Alert at once; the database never edits or deletes entries | Stop-the-line runbook R5: find the transaction, post a reversing entry, write a postmortem | CK1, U3 |
| FS-35 | Payments, capture attempts or refunds unresolved too long | CK5 and age alert | Reconciler retries inquiry with backoff | Escalate to manual review after the threshold; ask the PSP for its record | CK5, C3 |
| FS-36 | Reconciler is down, slow, or two run at the same time | Reconciler heartbeat; oldest unresolved age | Lease or advisory lock allows one active worker per batch; work is idempotent | Restart; backlog drains with a rate cap | C26 |

### Security and operations

| ID | Failure | Detect | Automatic | Recovery | Proof |
|---|---|---|---|---|---|
| FS-37 | Secret or certificate expires | Expiry alert in advance | Alerts at set days before expiry | Rotate with overlap (grace period) so nothing breaks | C21 |
| FS-38 | Secret or API key leaked | Anomaly alert, scanner hit, or report | Revoked key is rejected within the cache time | Runbook R6: revoke, rotate, review the audit log | O6 |
| FS-39 | Card-testing attack | Velocity rules; spike in small declines | Rate and velocity blocks; alert | Tighten rules; block sources | F1 |
| FS-40 | Slow-body clients, oversized bodies, connection floods | Connection and timeout counters | Kestrel limits cut them off | Block at the edge | L4 |
| FS-41 | Loss of a whole region | Region health | Not handled in version 1 (single region) | Restore from backup (RPO above 0); cross-region replica is a future decision | O5 (restore only) |

## Edge cases

These are not outages. They are unusual but valid situations that must give a defined result. All are covered by the edge-case test suite **C31** unless another test is named.

| ID | Situation | Defined behavior |
|---|---|---|
| E1 | Same idempotency key, different request body | Reject with 422; do not run |
| E2 | Same key while the first request is still running | Return 409 with `Retry-After`; do not start a second one |
| E3 | Client disconnects after transaction A | Processing continues; the result is stored; a retry with the same key returns it |
| E4 | Key reused after the retention period | Treated as a new request. Retention is at least 24 hours after the operation resolves, is documented to merchants, and never ends while the operation is unresolved ([D19](decisions.md#d19)) |
| E5 | Merchant retries with a new key after a timeout | Cannot be detected by key; guidance is to poll the payment ID or use one key per order. Optional: warn on a repeated merchant order reference (`Open`) |
| E6 | Refund on a payment that is not captured, or is `UNKNOWN` | Reject with a clear error |
| E7 | Refund larger than the remaining amount | Reject; the atomic reservation protects the total (C6) |
| E8 | Capture after the authorization expired or was voided | Reject; state machine forbids it |
| E9 | Late "declined" callback on a captured payment | Ignore, log, alert (FS-05) |
| E10 | PSP callback arrives before our transaction B commits | Conditional update finds the old state; event is parked and retried briefly, then reconciled |
| E11 | Zero, negative or very large amount | Reject at validation |
| E12 | Currency with a different number of decimals (for example JPY has 0, KWD has 3) | Integer minor units with a per-currency exponent; no floating point (U1) |
| E13 | Capture and refund requests at the same moment | Conditional updates serialize them; one wins, the other re-reads |
| E14 | Webhook delivered twice or out of order | Each event has an ID and a sequence; merchants are told to deduplicate; documented |
| E15 | Event larger than the size limit or schema mismatch | Rejected at publish; contract tests catch it in CI (U5) |
| E16 | Card data appears in logs or error messages | Redaction and a log scan (U8) |
| E17 | Tenant A uses a key against tenant B's data | Rejected by tenant checks (U6) |
| E18 | New attempt while another is live, or two at once | One proceeds; the unique index gives the other the current status or 409 with `Retry-After` (C37) |
| E19 | Late approval on an attempt already marked `FAILED` | Attempt moves to `REVIEW` and an alert fires, with no ledger entry. A payment in `AWAITING_METHOD` also moves to `REVIEW` (C38) |
| E20 | Customer abandons 3D Secure | Action window ends, state becomes `UNKNOWN`, the reconciler inquires, and the payment returns to `AWAITING_METHOD` (C39) |
| E21 | Retry of a declined request with the same key | Stored decline is returned, with no new PSP call. A new try needs a new key |
| E22 | Settlement line with no matching operation, or a different amount | Line is `UNMATCHED` or `MISMATCH`; a discrepancy pages; nothing is posted (C40) |
| E23 | Same settlement file ingested twice | The primary key makes it a no-op; no second ledger entry (C40) |
| E24 | Key older than 24 hours whose payment is still `UNKNOWN` | Key is kept, so a retry returns the current state and can't charge again (C41) |
| E25 | Same key used on a different route | Reject with 422; the hash covers method, route and body ([D31](decisions.md#d31)) |
| E26 | Fraud check blocks an attempt | Attempt `FAILED` with `fraud_blocked`, no PSP call, 402, counts toward the cap, and a retry with a new key can run ([D33](decisions.md#d33)) |
| E27 | Retry with the same key while the operation is `UNKNOWN` | Renders the current state; no PSP call and no new attempt ([D31](decisions.md#d31)) |
| E28 | Retry with the same key of a finished request | Stored answer, with no fraud check and no change to velocity counters ([D33](decisions.md#d33)) |
| E29 | Request refused before its work began (attempt live or cap reached), then retried with the same key | The refusal stored nothing, so the retry is evaluated fresh ([D31](decisions.md#d31)) |

## Runbooks

Short, ordered steps. Each runbook is rehearsed in a [drill](#drills) before it is trusted. Times are starting targets.

**R1. Database primary failed (FS-22)**
1. Confirm the standby was promoted and the old primary is fenced (cannot accept writes).
2. Check applications reconnected and error rate is falling.
3. Run CK1-CK6 against the new primary.
4. Rebuild a new standby; confirm synchronous replication is back.
5. Record the downtime against the RTO target.

**R2. PSP outage or slowdown (FS-02, FS-03)**
1. Confirm the breaker is open and the cause is on the PSP side (their status page, a probe call).
2. If a second PSP is allowed, confirm traffic is moving and watch its limits.
3. Tell merchants (status page): new payments may be declined or delayed.
4. When the PSP recovers, let the breaker close by itself; do not force it.
5. Wait for the reconciler to clear `UNKNOWN`; confirm CK2 and CK5.

**R3. Broker outage (FS-27)**
1. Confirm the outbox is growing and payments are still being accepted.
2. Do not delete anything. Watch disk on the database.
3. After recovery, let publishers drain at the capped rate; watch consumer lag.
4. Confirm CK4 and CK6.

**R4. Many payments or operations stuck unresolved (FS-35)**
1. Check whether the PSP inquiry works (a manual call by `attempt_id`).
2. Check the reconciler is running and its rate is not capped too low.
3. Past the threshold, move the remainder to the manual review queue.
4. For each, record the PSP's answer and post any missing ledger entries.

**R5. Ledger imbalance (FS-34): stop-the-line**
1. Pause refunds and payouts if the imbalance is growing; keep accepting payments only if the bad path is isolated.
2. Find the transaction IDs from the audit query.
3. Never edit or delete ledger rows. Post a reversing entry and a correct entry.
4. Write a postmortem and add a test that would have caught it.

**R6. Secret or key leaked (FS-38)**
1. Revoke the key or secret; rotate with a short grace period only if the leak is minor.
2. Review the audit log for use of the leaked secret.
3. Notify affected merchants if their data or keys were exposed.
4. Rescan code and logs for the secret; record the incident.

**R7. Restart after a full outage (FS-18)**
1. Bring up the database, then the broker, then services, in that order.
2. Keep the limiter low; raise the allowed rate in steps while watching latency and pool wait.
3. Resume consumers and reconciler at capped rates.
4. Run CK1-CK6 before announcing recovery.

## Drills

| Order | Drill | Tests | Notes |
|---|---|---|---|
| 1 | In a test environment, break one thing at a time under 1k TPS | C3, C4, C5, C6, C15, C36, C37, C38, C41, C43, C44, C45, C46 | Money-correctness faults first |
| 2 | PSP and dependency faults under 5k TPS | C9, C12, C13, C14, C16, C23, C32, C39, C40, C47, C48 | Use the mock PSP in slow, error and down modes |
| 3 | Data faults under 5k TPS | C1, C2, C7, C22, C29, C34, C35, C49 | Database slow, pool exhausted, failover, disk, lock races, read flood |
| 4 | Messaging faults | C8, C17, C18, C26, C27 | Broker down, poison, webhook endpoints failing |
| 5 | Platform faults | C10, C11, C19, C20, C24, C25, C28, C30, and C33 only if cells are adopted | Node, zone, release, cache, clock, restart, routing |
| 6 | Security and recovery | C21, C42, O5, O6, F1 | Rotation, restore, leak response |

Rules for every drill: define the expected result first, run CK1-CK6 afterwards, record the run ID, and file a fix for anything that surprised the team. A drill does not count if it only passed because the fault was not really injected (verify the fault happened).

## Open questions

- **Fallback policy for the fraud service** (allow, hold or reject, by risk tier) needs a business owner.
- **A warning on repeated order references (E5)** is optional and not designed yet.
- **Manual review tooling** for `REVIEW` and FS-35 needs a simple internal screen; not designed.
- **Region loss (FS-41)** is out of scope for version 1.
- **The order of protection** is `Proposed` until the owner confirms it.
- **All times and thresholds** are starting values until measured.
