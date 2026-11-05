# Deadlock Handling

How Open Payments prevents, detects and recovers from database deadlocks at high load on PostgreSQL.

Status: Draft · Last updated: 2026-11-05

Settings in [Database standards](engineering-standards.md#database) are `Committed`. Everything under [Proposed automation](#proposed-automation) is `Proposed`. Nothing here is measured yet.

## Where deadlocks can happen here

The [ledger](architecture.md#ledger) is append-only and updates no balance column on the payment path, so the classic "two accounts, opposite order" deadlock is mostly designed out. Hot accounts are split into 64 sub-accounts. The risks that remain, to be confirmed by L1, C6 and C34:

- **The same payment row.** A capture, a refund and a reconciler or callback update can all touch one `payments` row, plus `refunds` and ledger inserts, in different statement orders.
- **Batch jobs.** The reconciler, projector and outbox cleanup can update many rows without a stable `ORDER BY`.
- **Foreign keys.** `payment_attempts` and `refunds` reference `payments`. A foreign-key check takes a key-share lock on the parent, which does not conflict with an update that leaves the key unchanged. The risk is low, but check the deadlock log rather than assume.
- **Balance and reservation locks.** Not in v1 ([D38](decisions.md#d38)). If fees or payouts bring back the [advisory lock](decisions.md#d9), take it first, every time.

## How a deadlock forms

A deadlock happens when two or more transactions each hold a lock the other needs. PostgreSQL finds the cycle and aborts one victim with `40P01`.

```
T1: holds row A, wants row B
T2: holds row B, wants row A
=> cycle; PostgreSQL aborts one
```

It needs all four conditions: exclusive locks, holding one lock while waiting for another, no preemption, and a circular wait. Removing any one prevents it. With a relational database you cannot prove zero, because foreign keys, unique indexes and `ON CONFLICT` take implicit locks. So prevent what you can and keep a safe retry path.

**Why load makes it much worse.** Deadlock rate grows roughly with the square of throughput, so 5x the load gives about 25x the deadlocks. Causes compound:

| Cause | Effect |
|---|---|
| More overlapping transactions (Little's Law) | 5x load means 5x more in flight |
| Latency feedback loop | Busier database, longer lock holds, more concurrency, slower still |
| Hot rows | A permanent lock queue, where any order difference deadlocks |
| Latent lock-order bugs | A tiny timing window at low load is hit constantly at high load |
| Pool saturation | Transactions sit idle while holding locks |

## Prevention

1. **Use one lock order.** Update `payments`, then write `refunds` and other child rows, then ledger and outbox rows. Batch jobs use a stable `ORDER BY`:

   ```sql
   select id from payments
    where id = any($ids)
    order by id
    for update;
   ```

2. **Keep transactions short.** At most about 5 statements, with no network call inside. Do PSP calls before the transaction, and use the outbox for side effects.
3. **Lock fewer rows.** Insert ledger entries rather than updating balances, and make sure queries use indexes.
4. **Use atomic conditional updates** instead of read-then-write. A plain `SELECT` takes no row lock in PostgreSQL, so read-then-update risks a lost update rather than a deadlock. The [transition rules](payment-lifecycle.md#transition-rules) avoid both.
5. **Split any new hot row.** Use the 64 sub-account pattern, or micro-batch postings to it.
6. **Optimistic concurrency is an option where contention is low:** a `version` column with compare-and-swap, retried on conflict.

If tests still show deadlocks on a hot row, the fallback is a single writer per account, partitioned by account ID. It serializes work without row locks at the cost of a new component, so it stays `Proposed` until measurements justify it.

**Avoid:** external calls while holding locks, immediate retries without jitter, and cross-service locks, which a single database detector cannot see.

## Handling a deadlock

Retry the whole transaction, never a single statement.

```csharp
// sketch: every write path goes through one runner, so no one can forget it
public async Task<T> RunAsync<T>(Func<NpgsqlTransaction, Task<T>> work, CancellationToken ct)
{
    for (var attempt = 0; ; attempt++)
    {
        await using var conn = await _dataSource.OpenConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            var result = await work(tx);
            await CommitWithWaitLimitAsync(tx); // no cancel is ever sent; see D28
            return result;
        }
        catch (PostgresException e) when (
            e.SqlState is PostgresErrorCodes.DeadlockDetected or PostgresErrorCodes.SerializationFailure
            && attempt < MaxRetries            // 2
            && _retryBudget.TryAcquire())
        {
            // full jitter: 5 ms * 2^attempt, capped at 200 ms
            var capMs = Math.Min(200, 5 * (1 << attempt));
            await Task.Delay(Random.Shared.Next(capMs + 1), ct);
        }
        // exhausted retries and 55P03 become 503 with Retry-After;
        // other errors, such as a duplicate-key violation, keep their own handling
    }
}
```

**Rules:**

- Retry only `40001` and `40P01`. Never retry business errors or `55P03`.
- A failed `COMMIT` is not a retry case. Its outcome is unknown, so read the row ([D28](decisions.md#d28)).
- Store the idempotency result in the same transaction, so a retry cannot double-charge.
- Keep side effects out of the transaction; use the outbox.
- Re-read state on each attempt.
- When retries or the budget run out, return `503` with `Retry-After`. The client retries with the same idempotency key.
- Watch the retry rate. A rising rate means retries are adding contention.

## Proposed automation

All `Proposed`. None of it is in the design yet, and each layer needs a decision backed by measurements. Overload is always shed, never queued ([D13](decisions.md#d13)).

| Layer | What it does | Starting values |
|---|---|---|
| Retry budget size | Limits retries to a small share of traffic, then fails fast | 10% of requests per 10 s |
| Circuit breaker, per hot key or route | Rejects writes on the affected path with 503, so the database can drain | Opens above 5% deadlock rate for 10 s; half-open after 15 s at 5% of traffic; closes below 1% |
| Adaptive concurrency limit | Backs off fast and probes up slowly, breaking the latency feedback loop. Excess is shed | Every 1 s: multiply the limit by 0.7 if deadlocks exceed 1% or p99 is over target, otherwise add 1; stay between MIN and MAX |
| Hot-key detection | Finds accounts with many lock waits and moves them to the single-writer fallback or sub-accounts | More than 50 lock waits per second; return after 5 min below a lower threshold |
| Database watchdog | Finds and clears stuck blockers, and collects evidence | Terminate blockers older than 2 s; alert when the same query pair recurs. Pool wait above threshold: shed and reduce the limiter |

**Degradation tiers** move automatically and stay at least 60 s in a tier. Shedding follows the [order of protection](failure-scenarios.md#rules-for-every-failure).

| Tier | Deadlock rate | Action |
|---|---|---|
| 0 Normal | Below 0.1% | None |
| 1 Elevated | 0.1% to 1% | Shrink the concurrency limit; route newly hot keys to the fallback |
| 2 High | 1% to 5% | Shed reports and exports, slow webhooks, tighten the retry budget |
| 3 Critical | Above 5% | Open the breaker on the affected path and shed with 503, including refunds and captures on that path |

**Escalate to a person when:** tier 2 lasts over 10 min, the breaker stays open over 15 min, the retry budget is exhausted for over 5 min, or the watchdog repeatedly kills the same query. For the single-writer fallback: Queue lag keeps growing.

**Watchdog query:**

```sql
select blocked.pid                as blocked_pid,
       blocked.query              as blocked_query,
       blocker.pid                as blocker_pid,
       blocker.query              as blocker_query,
       now() - blocker.xact_start as blocker_age
from pg_stat_activity blocked
join lateral unnest(pg_blocking_pids(blocked.pid)) as b(pid) on true
join pg_stat_activity blocker on blocker.pid = b.pid;
```

The [role-level timeouts](engineering-standards.md#database) must protect the system even if the watchdog is down.

## Observability

- Set `log_lock_waits = on`, and read `deadlock detected` entries in the server log.
- Track `pg_stat_database.deadlocks`, along with `40P01` and `55P03` rates, retries and retries exhausted, transaction p50 and p99, pool wait time, and the top rows by update count.

A healthy `deadlock_timeout` makes these counters meaningful. If `55P03` rises while deadlocks stay at zero, check that `deadlock_timeout` is still below `lock_timeout` ([D1](decisions.md#d1)).

| Symptom | Fix |
|---|---|
| The same two queries recur in deadlock logs | Lock-order bug: fix the order, not just the retry |
| Deadlocks cluster on a few rows | Split, batch or use the single-writer fallback |
| p99 jumps with load | Shorten transactions, fix pool sizing |
| Rare residual deadlocks | Idempotent retry with jitter |

## Testing

- Use skewed traffic, for example 80% on 1% of merchants. Uniform tests hide hot-row deadlocks.
- Record `pg_stat_database.deadlocks` at every [ramp step](scaling-playbook.md#ramp-plan), and run above target to see the curve.
- Run C6 and [C34](verification.md#chaos-and-failure-tests) to force races on one payment, and confirm no double charge after forced deadlocks.
- If automation is adopted: confirm tiers and breaker engage and recover, confirm no flapping, and kill the watchdog to confirm database timeouts still protect the system.
