# Deadlock Handling at High TPS

How to prevent, detect and recover from database deadlocks in a payment system running at ~5,000 TPS.

Status: draft v1 · Companion to `payment-system-design.md` (sections 9 and 14.2), `payment-lifecycle.md` and `failure-scenarios.md` (FS-42). Nothing here is measured yet.

## 0. How this fits the Open Payments design

The design (`payment-system-design.md`) is the source of truth: PostgreSQL, C# / .NET 8 with Npgsql. Sections 1 to 7 and 10 to 11 explain deadlocks in general terms, and some examples use an `accounts.balance` column and other databases for illustration. The project differs in these ways:

| Topic | In the design | Effect on this document |
|---|---|---|
| Balances | Append-only ledger; **no balance column is updated on the payment path** (section 9) | The classic "two accounts, opposite order" deadlock (Scenarios 1 and 2) is mostly designed out. Keep it as a rule for any code that does update shared rows. |
| Hot accounts | Split into 64 sub-accounts by `hash(payment_id) % 64` | Use 64, not an arbitrary shard count. |
| Transaction shape | At most about 5 statements, no network call inside (section 14.2) | Same as prevention rule 5.2. |
| Timeouts | `lock_timeout` 200 ms, `statement_timeout` 1 s, `idle_in_transaction_session_timeout` 2 s | Values in section 8 follow the design. |
| Retries | On SQLSTATE 40001 and 40P01, at most **2** retries with jitter, inside a retry budget | Section 7 and 8 follow the design. |
| Database | PostgreSQL only | MySQL, SQL Server and Oracle rows are for reference. |

Where deadlocks can still happen in this design (analysis, not measured; test L1 and C6 will confirm):

- A capture, a refund and a reconciler or PSP-callback update all conditionally update the **same `payments` row**, plus `refunds` and ledger inserts, in different statement orders.
- Batch jobs (reconciler, projector, outbox cleanup) that update many rows without a stable `ORDER BY`.
- Rows with foreign keys to `payments` (`payment_attempts`, `refunds`, `ledger_transactions`) being inserted while the parent row is updated. Updates that do not change the key take a lock that does not conflict with the foreign-key check in PostgreSQL, so this is a lower risk, but check the deadlock log rather than assume.
- Any future balance or reservation row (for example the payout reservation in design section 9) that several payments update.

`deadlock_timeout` of 200 ms and the breaker, limiter and degradation tiers in section 8 are **proposals** that the design does not contain yet.

## 1. What is a deadlock

A deadlock happens when two or more transactions each hold a lock the other needs, so none can proceed. It requires all four Coffman conditions:

1. **Mutual exclusion**: a resource (row lock) is held exclusively.
2. **Hold-and-wait**: a transaction holds one lock while requesting another.
3. **No preemption**: locks cannot be taken away.
4. **Circular wait**: a cycle of transactions each waiting on the next.

Break any one condition and deadlock is impossible.

Classic payment example:

```
T1: transfer A -> B     locks A, then wants B
T2: transfer B -> A     locks B, then wants A
=> cycle, DB aborts one
```

## 2. Understand a payment deadlock

### 2.1 Anatomy

A payment touches several rows in one transaction: the payer account, the payee account, a fee account and the ledger. A deadlock needs two payments that touch an overlapping set of rows **in a different order**.

```
Accounts: A (balance 100), B (balance 50)

T1: transfer 10 from A to B        T2: transfer 5 from B to A
```

### 2.2 Timeline

| Time | T1 (A -> B) | T2 (B -> A) | Lock state |
|---|---|---|---|
| t1 | `BEGIN` | `BEGIN` | none |
| t2 | `UPDATE accounts SET balance = balance - 10 WHERE id = 'A'` | | T1 holds A |
| t3 | | `UPDATE accounts SET balance = balance - 5 WHERE id = 'B'` | T2 holds B |
| t4 | `UPDATE accounts SET balance = balance + 10 WHERE id = 'B'` | | T1 waits for B (held by T2) |
| t5 | | `UPDATE accounts SET balance = balance + 5 WHERE id = 'A'` | T2 waits for A (held by T1) |
| t6 | **cycle: T1 -> B -> T2 -> A -> T1** | | DB detects it |
| t7 | rolled back (victim) | continues and commits | T1 gets `40P01` / `1213` |

Neither transaction can finish because each needs a lock the other holds, so the DB aborts one.

### 2.3 Wait-for graph

```
T1 --waits for B--> T2
 ^                   |
 |                   |
 +---waits for A-----+
```

A cycle in this graph is a deadlock. The detector looks for cycles and picks a victim.

### 2.4 What it looks like in the log (PostgreSQL)

```
ERROR:  deadlock detected
DETAIL: Process 4121 waits for ShareLock on transaction 9120; blocked by process 4130.
        Process 4130 waits for ShareLock on transaction 9118; blocked by process 4121.
HINT:   See server log for query details.
CONTEXT: while updating tuple (0,2) in relation "accounts"
```

### 2.5 The fix in one line

Lock both rows up front in a fixed order, so both transactions queue on the same row first and no cycle can form:

```sql
SELECT id FROM accounts WHERE id IN ('A', 'B') ORDER BY id FOR UPDATE;
-- now do both updates
```

## 3. Scenarios

### Scenario 1: Bidirectional transfer (lock-order inversion)

- **Setup:** two users send money to each other at the same moment.
- **Cause:** T1 locks A then B, T2 locks B then A (the timeline in section 2).
- **Fix:** sort account IDs and lock in ascending order.
- **Why it shows at scale:** at 1k TPS the two transfers rarely overlap; at 5k the window is hit constantly.

### Scenario 2: Hot merchant account

- **Setup:** a flash sale; thousands of payments credit the same merchant account.
- **Cause:** every payment updates `merchant_balance`. Payments also touch a fee account in different orders (some `payer, merchant, fee`, others `payer, fee, merchant`). A permanent queue forms on the merchant row, and any order difference becomes a cycle.
- **Fix:**
  - Shard the merchant balance into N sub-balances (`hash(payment_id) % 64`, as in design section 9) and pick one by hash.
  - Or append ledger entries and aggregate balances in micro-batches.
  - Use one lock order in every code path.

### Scenario 3: Read-then-update lock upgrade

- **Setup:** two payments debit the same account.
- **Cause:** both run `SELECT balance` (shared lock or snapshot), then both try `UPDATE`. Each waits for the other to release its shared lock.

```
T1: SELECT balance FROM accounts WHERE id='A';   -- shared lock
T2: SELECT balance FROM accounts WHERE id='A';   -- shared lock
T1: UPDATE accounts SET balance = ... WHERE id='A';  -- waits for T2's lock
T2: UPDATE accounts SET balance = ... WHERE id='A';  -- waits for T1's lock => deadlock
```

- **Fix:** take the exclusive lock first with `SELECT ... FOR UPDATE`, or use one atomic statement: `UPDATE ... SET balance = balance - :amt WHERE id = :id AND balance >= :amt`.

### Scenario 4: Foreign key and index locks

- **Setup:** inserting into `ledger_entries` (FK to `accounts`) while another transaction updates the same `accounts` row.
- **Cause:** the FK check takes a shared lock on the parent row, and unique index or gap locks (InnoDB) conflict with concurrent inserts. These locks are implicit, so the lock order is not visible in your code.
- **Fix:** read the deadlock graph to find the implicit lock, then change the order of statements or relax the constraint. Use read committed to reduce gap locks. Keep transactions short.

### Scenario 5: External call inside the transaction

- **Setup:** the code locks the payer row, calls a fraud or bank API (200 ms), then locks the payee row.
- **Cause:** locks are held for the whole external call. In-flight transactions pile up (Little's Law) and pool exhaustion turns waits into cycles.
- **Fix:** do the external call before `BEGIN`, or use a saga: reserve funds, call the bank, then confirm or compensate in separate short transactions.

### Scenario 6: Retry storm

- **Setup:** a deadlock victim retries immediately with no backoff.
- **Cause:** both victims retry at the same instant and deadlock again. Each retry adds load, which raises latency and creates more deadlocks.
- **Fix:** exponential backoff with jitter, a retry cap, an idempotency key, and a circuit breaker on retries.

### Scenario summary

| # | Scenario | Root cause | Fix |
|---|---|---|---|
| 1 | Bidirectional transfer | Lock-order inversion | Canonical lock order |
| 2 | Hot merchant account | Hot row plus order differences | Shard, batch, one order |
| 3 | Read-then-update | Lock upgrade | `FOR UPDATE` or atomic `UPDATE` |
| 4 | FK / index locks | Implicit locks | Read graph, reorder, read committed |
| 5 | External call in txn | Long lock hold | Call outside txn, saga |
| 6 | Retry storm | No jitter / no cap | Backoff, cap, idempotency, breaker |

## 4. Why 1,000 TPS is fine but 5,000 TPS is not

Deadlocks grow much faster than load.

```
deadlock rate  ∝  TPS² × (locks per txn)⁵ × lock hold time / DB size²
```

5x the TPS gives roughly 25x the deadlocks.

Causes that compound:

| Cause | Effect |
|---|---|
| Little's Law: `in-flight txns = TPS × duration` | 5x TPS means 5x more overlapping transactions |
| Latency feedback loop | Busier DB, slower txns, longer lock holds, more concurrency, slower still |
| Hot rows (merchant, fee, settlement accounts) | Permanent lock queue; any lock-order difference deadlocks |
| Latent lock-order bugs | Timing window is tiny at 1k TPS, constantly hit at 5k |
| Pool saturation | Transactions sit idle while holding locks |

## 5. Prevention

### 5.1 Consistent lock ordering

Always lock rows in a canonical order, such as ascending account ID.

```sql
SELECT * FROM accounts
WHERE id IN (:from, :to)
ORDER BY id
FOR UPDATE;
```

### 5.2 Keep transactions short

- No network or external API calls while holding locks.
- Validate first, then open the transaction, write and commit.
- Use the outbox pattern for side effects (webhooks, events).

### 5.3 Lock fewer rows

- Append-only ledger: insert entries instead of updating balances in place.
- Make sure queries use indexes, so no range or gap locks are taken.
- Use atomic updates instead of read-then-write:

```sql
UPDATE accounts
SET balance = balance - :amt
WHERE id = :id AND balance >= :amt;
```

### 5.4 Remove hot-row contention

- **Shard hot accounts** into N sub-balances chosen by hash; sum on read.
- **Batch** postings to hot accounts in micro-batches (for example every 50 ms).
- **Single writer per account**: partition by account ID (Kafka partition or actor), so operations on one account are serialized without locks.

### 5.5 Optimistic concurrency

Use a `version` column with compare-and-swap and retry on conflict. Works well at low contention; combine with sharding on hot rows.

## 6. Getting to true zero deadlocks

Deadlock-free by construction requires removing a Coffman condition:

| Approach | Condition broken |
|---|---|
| Never hold more than one lock (saga: debit, then credit in a separate txn) | Hold-and-wait |
| Strict global lock ordering | Circular wait |
| Single writer per entity (LMAX, TigerBeetle style) | Lock contention |
| Optimistic / lock-free CAS | Hold-and-wait, no-preemption |
| Deterministic serial engines (Calvin, VoltDB) | Runtime lock conflicts |

Limits of the claim:

- With an RDBMS you cannot *prove* zero. Foreign keys, unique indexes, gap locks and `ON CONFLICT` take implicit locks.
- Distributed deadlocks across services (2PC, distributed locks) are invisible to a single DB detector. Avoid cross-service locks; use sagas.
- Application-level locks, connection-pool exhaustion and thread-pool starvation cause deadlock-like hangs.

**Recommended for a ledger at 5k TPS:** single-writer per account, append-only entries, sagas for cross-account transfers. On an RDBMS, aim for effectively zero and keep the retry path.

## 7. Handling a deadlock when it happens

The database picks a victim, rolls it back and returns an error.

| DB | Error |
|---|---|
| PostgreSQL | `40P01 deadlock_detected` |
| MySQL / InnoDB | `1213 ER_LOCK_DEADLOCK` |
| SQL Server | `1205` |
| Oracle | `ORA-00060` |

### 7.1 Retry the whole transaction

```python
MAX_RETRIES = 3   # 1 try + at most 2 retries (design 14.2)

def run_tx(fn):
    for attempt in range(MAX_RETRIES):
        try:
            with db.transaction():
                return fn()
        except DeadlockError:
            if attempt == MAX_RETRIES - 1:
                raise
            sleep(random.uniform(0, 0.01 * 2 ** attempt))  # backoff + jitter
```

Rules:

- **Idempotency**: use an idempotency key with a unique constraint so a retry cannot double-charge.
- **Backoff with jitter**, otherwise victims retry together and deadlock again.
- **Cap retries** at 2 (design section 14.2), then return a retryable error.
- **No side effects inside the transaction**; a retry would repeat them.
- **Re-read state** on each attempt; do not reuse values from the failed one.
- **Retry only** deadlocks and serialization failures (`40001`, `40P01`), never business errors such as insufficient funds.

### 7.2 Client response

If retries are exhausted, return `503` (or `409`) with `Retry-After`. The client retries with the same idempotency key.

### 7.3 Reduce the damage

- Set `lock_timeout` and a shorter `deadlock_timeout` (Postgres default is 1s) so victims fail fast.
- Keep transactions short so each retry is cheap.
- Watch the retry rate. A rising rate means retries add contention (retry storm). Add a circuit breaker or retry rate limit.

## 8. Automated recovery and mitigation

Manual retry code is the last line of defense. At 5k TPS the system should detect, recover and adapt on its own, without a human in the loop.

### 8.1 Architecture

```
Client
  |  (Idempotency-Key)
  v
API gateway --- rate limit / load shedding ------------+
  |                                                    |
  v                                                    |
Payment service                                        |
  |  1. Transaction runner (retry + backoff + budget)  |
  |  2. Hot-key router (single-writer queue)           |
  |  3. Adaptive concurrency limiter                   |
  |  4. Circuit breaker                                |
  v                                                    |
Database <--- Watchdog (kills blockers, collects data) |
  |                                                    |
  +--> Metrics --> Alerts --> Auto-mitigation <--------+
```

### 8.2 Layer 1: Automatic transaction retry

A single wrapper that every write path uses, so no developer can forget it.

```python
RETRYABLE = {"40P01", "40001", "55P03"}   # deadlock, serialization, lock timeout

def run_tx(fn, idempotency_key):
    if already_applied(idempotency_key):          # safe replay
        return stored_result(idempotency_key)

    for attempt in range(MAX_RETRIES):            # 3 (1 try + 2 retries)
        if not retry_budget.try_acquire():        # see 8.3
            raise Overloaded()
        try:
            with db.transaction(isolation="READ COMMITTED"):
                result = fn()
                save_result(idempotency_key, result)   # same txn, unique key
                return result
        except DbError as e:
            if e.sqlstate not in RETRYABLE:
                raise
            metrics.incr("tx.retry", reason=e.sqlstate)
            sleep(jittered_backoff(attempt))
    raise RetriesExhausted()                      # caller returns 503 + Retry-After

def jittered_backoff(attempt):
    return random.uniform(0, min(0.2, 0.005 * 2 ** attempt))   # full jitter
```

Requirements:

- Idempotency key stored in the same transaction as the payment (unique constraint).
- Side effects only after commit (outbox).
- Only retry the SQLSTATEs in `RETRYABLE`.

### 8.3 Layer 2: Retry budget (prevents retry storms)

Allow retries only as a small fraction of total traffic.

```
retry budget = 10% of requests in the last 10 seconds
```

If the budget is exhausted, fail fast with `503` instead of retrying. This stops a deadlock burst from multiplying load.

### 8.4 Layer 3: Circuit breaker

| State | Condition | Behavior |
|---|---|---|
| Closed | deadlock rate < 1% | Normal operation |
| Open | deadlock rate > 5% for 10 s | Reject new writes on the affected path with `503` + `Retry-After`; let the DB drain |
| Half-open | after 15 s | Allow 5% of traffic; close if the rate recovers, else reopen |

Scope the breaker per hot key or per route, so one hot merchant does not stop all payments.

### 8.5 Layer 4: Adaptive concurrency limiter

Cap in-flight transactions and adjust the cap automatically (AIMD, like TCP congestion control).

```
every 1 s:
    if deadlock_rate > 1% or p99_txn_ms > target:
        limit = max(MIN, limit * 0.7)      # back off fast
    else:
        limit = min(MAX, limit + 1)        # probe up slowly
```

Requests over the limit wait in a bounded queue or are shed. This breaks the latency feedback loop from section 4: fewer concurrent transactions means shorter lock holds, fewer deadlocks and lower latency.

### 8.6 Layer 5: Hot-key detection and automatic isolation

Detect hot accounts and move them to a deadlock-free path.

1. Count lock waits and deadlocks per account ID (sliding window).
2. When an account exceeds a threshold (for example more than 50 lock waits per second), mark it **hot** in a config store (Redis or a feature flag).
3. The router sends writes for hot accounts to a **single-writer queue** partitioned by account ID, so they are serialized without locks.
4. When the rate drops below a lower threshold for 5 minutes, return the account to the normal path (hysteresis avoids flapping).

For merchant-style accounts, the same detector can trigger **sharded balances**: spread writes across N sub-balances and aggregate on read.

### 8.7 Layer 6: Database watchdog

A background job that finds and clears stuck work, so one bad transaction cannot cascade.

Find blocking chains (PostgreSQL):

```sql
SELECT blocked.pid                AS blocked_pid,
       blocked.query              AS blocked_query,
       blocker.pid                AS blocker_pid,
       blocker.query              AS blocker_query,
       now() - blocker.xact_start AS blocker_age
FROM pg_stat_activity blocked
JOIN LATERAL unnest(pg_blocking_pids(blocked.pid)) AS b(pid) ON true
JOIN pg_stat_activity blocker ON blocker.pid = b.pid;
```

Rules the watchdog applies:

| Condition | Action |
|---|---|
| Blocker age > 2 s (`idle in transaction` or long running) | `pg_terminate_backend(blocker_pid)` and log the query |
| Same query pair in deadlocks > N times per minute | Raise an alert with both statements attached |
| Connection pool wait > threshold | Shed load and reduce the limiter |

Also set database-level guards so the DB acts even if the watchdog is down:

```sql
ALTER ROLE payment_app SET lock_timeout = '200ms';
ALTER ROLE payment_app SET statement_timeout = '1s';
ALTER ROLE payment_app SET idle_in_transaction_session_timeout = '2s';
ALTER SYSTEM SET deadlock_timeout = '200ms';
```

### 8.8 Layer 7: Graceful degradation tiers

When mitigation is not enough, degrade in steps instead of failing everything.

| Tier | Trigger | Action |
|---|---|---|
| 0 Normal | deadlock rate < 0.1% | None |
| 1 Elevated | 0.1% to 1% | Shrink the concurrency limit; route newly hot keys to single-writer |
| 2 High | 1% to 5% | Shed low-priority traffic (reports, refunds, batch); tighten retry budget |
| 3 Critical | > 5% | Open breaker on affected path; accept payments into a durable queue and process asynchronously |

The system moves up and down the tiers automatically, with hysteresis (stay in a tier for at least 60 s).

### 8.9 Layer 8: Failed work is never lost

- **Dead letter queue:** after retries are exhausted, store the request (with its idempotency key) in a DLQ, and a worker replays it later at low concurrency.
- **Reconciliation job:** periodically compare payment records against ledger entries and repair gaps.
- **Client contract:** `503` plus `Retry-After`, and the client retries with the same idempotency key.

### 8.10 Recommended thresholds (starting values, tune with load tests)

| Setting | Value |
|---|---|
| Max retries per transaction | 2 |
| Backoff | full jitter, `5 ms * 2^attempt`, capped at 200 ms |
| Retry budget | 10% of requests per 10 s |
| `lock_timeout` | 200 ms |
| `statement_timeout` | 1 s |
| `idle_in_transaction_session_timeout` | 2 s |
| `deadlock_timeout` | 200 ms (proposal, not in the design yet) |
| Breaker open | deadlock rate > 5% for 10 s |
| Breaker half-open after | 15 s |
| Hot-key threshold | > 50 lock waits per second per key |
| Tier hysteresis | 60 s |

### 8.11 Automated response runbook

| Signal | Automatic response | Escalate to a human when |
|---|---|---|
| Deadlock rate up | Shrink concurrency limit; retry with jitter | Tier 2 lasts more than 10 min |
| Same account in many deadlocks | Mark hot, route to single-writer queue | Queue lag keeps growing |
| Long blocker found | Watchdog terminates it, logs the query | Same query is killed repeatedly |
| Retry budget exhausted | Return `503` + `Retry-After` | Sustained for more than 5 min |
| Breaker open | Queue payments durably, drain later | Open for more than 15 min |
| DLQ growing | Replay at low concurrency | DLQ age exceeds SLA |

### 8.12 Testing the automation

- Chaos test: inject bidirectional transfers and a hot-key spike at 5k, 7.5k and 10k TPS, and confirm the tiers and breaker engage and recover.
- Verify idempotency under retry: no double charges after forced deadlocks.
- Kill the watchdog and confirm the DB-level timeouts still protect the system.
- Confirm hysteresis: no flapping between tiers or on and off hot-key routing.

## 9. Observability and root cause

| DB | How |
|---|---|
| PostgreSQL | `log_lock_waits = on`; read `deadlock detected` log entries; `pg_stat_database.deadlocks` |
| MySQL | `SHOW ENGINE INNODB STATUS` (`LATEST DETECTED DEADLOCK`); `innodb_print_all_deadlocks = ON` |

Track and alert on:

- Deadlock count and rate
- Retry rate and retries exhausted
- p50 / p99 transaction duration
- Connection pool wait time
- Top rows by update count (hot rows)

If the same two queries or tables recur in the deadlock graphs, it is a lock-order bug or a hot row. Fix the design, not just the retry code.

## 10. Diagnosis cheat sheet

| Symptom | Fix |
|---|---|
| Deadlocks between two-account transfers | Canonical lock ordering |
| Deadlocks clustered on a few rows | Shard, batch, or single-writer queue |
| Deadlocks from read-then-update | Atomic `UPDATE` or `SELECT ... FOR UPDATE` up front |
| p99 latency jumps with load | Shorten transactions, fix pool sizing, break the feedback loop |
| Rare residual deadlocks | Idempotent retry with jittered backoff |

## 11. Load testing

- Use skewed traffic (for example 80% of traffic on 1% of accounts), not uniform. Uniform tests hide hot-row deadlocks.
- Run at and above target TPS (5k, 7.5k, 10k) and watch the deadlock rate curve.
- Inject bidirectional transfers (A to B and B to A) explicitly.
- In this project use test L1 (ramp per `scaling-playbook.md`, with the hot-merchant scenario) and C6 (50 concurrent refunds above the captured amount), and add a capture-versus-refund-versus-reconciler race on the same payment. Record the deadlock count (`pg_stat_database.deadlocks`) at every ramp step.

## Summary

1. Prevent: consistent lock order, short transactions, fewer locks, no hot rows.
2. Design out: in this project the append-only ledger and 64 sub-accounts already remove most hot-row locking; single writer per account plus sagas is the fallback if tests still show deadlocks.
3. Recover: idempotent whole-transaction retry with jitter and a cap.
4. Automate: retry budget, circuit breaker, adaptive concurrency limit, hot-key isolation, watchdog and degradation tiers.
5. Observe: read the deadlock graphs and fix the recurring pattern.
