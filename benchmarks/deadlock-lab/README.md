# Deadlock Recovery Lab

Two accounts, 100 concurrent transfers, run through the production `TxRunner`. It shows the classic opposite-order deadlock, that the runner's retry recovers from it without losing or duplicating money, and that a consistent lock order removes it. It's a lab, not part of `OpenPayments.slnx`: Open Payments has no balances or transfers ([D38](../../docs/decisions.md#d38)), so the lab brings its own schema.

Status: laptop preview · Run date: 2026-11-09 · Code: `TxRunner` at `786d8ef`

```bash
benchmarks/deadlock-lab/run.sh 5
```

It starts a throwaway PostgreSQL 18.6 container on port 35432, runs 5 rounds per mode, prints the comparison, keeps the server's deadlock reports in `deadlocks.log`, and removes the container. `DEADLOCK_TIMEOUT` overrides the server setting; the default is D1's 100 ms.

## What it does

| Part | How |
|---|---|
| Accounts | `lab_accounts`, two rows of 10,000, `check (balance_minor >= 0)`, so an overdraft fails with `23514` and never goes negative |
| Transfers | 50 A→B and 50 B→A started together, amounts 1-499, plus one larger than any balance, which must be refused |
| One transfer | One `TxRunner.RunAsync` transaction: claim the idempotency key in `lab_transfers` (`on conflict do nothing`), debit, `pg_sleep(5 ms)` in both modes, credit, two ledger rows |
| Bad order (`unordered`) | Each transfer updates its source first, so opposite directions take the two rows in opposite orders |
| Fix (`ordered`) | Lock both rows first, lowest id first, with `FOR NO KEY UPDATE`. Plain `FOR UPDATE` would still deadlock, through the `FOR KEY SHARE` locks the `lab_transfers` foreign keys take on both accounts |
| Retry | The production runner: `40P01` and `40001` only, at most 2 retries with jitter, then `BusyException`. The lab acts as the client of the 503: it resubmits with the same key, at most 5 times |
| Replays | After each round, 10 committed transfers are resubmitted with their keys; each must change nothing |

**Checked every round:** the total stays 20,000; no balance is negative; one `lab_transfers` row per committed transfer; every replay is a no-op; every transfer has exactly two ledger rows summing to zero, with the right amount; each balance equals its opening balance plus its ledger.

**Observed:** `pg_stat_database.deadlocks` before and after each round, the runner's `op_tx_retries_total{sqlstate}` counter, client resubmits by `BusyException` reason, the server's `deadlock detected` reports, and wall time and per-transfer latency including resubmits.

## Results

5 rounds per mode, server `deadlock_timeout` 100 ms (D1). Latency is per transfer, including retries and resubmits.

| Mode | Wall (median) | p50 | p99 | Server deadlocks | `40P01` retries | Client resubmits | Committed | Invariants |
|---|---:|---:|---:|---:|---:|---:|---:|---|
| unordered | 55,668 ms | 35,979 ms | 55,658 ms | 2,591 | 2,591 | 698 | 494 of 500 | all hold |
| ordered | 942 ms | 485 ms | 932 ms | 0 | 0 | 0 | 500 of 500 | all hold |

- **Retries are observable and complete:** the runner counted exactly as many `40P01` retries as PostgreSQL counted deadlocks. Every resubmit was `retry_exhausted`: all three attempts of a transfer deadlocked.
- **Recovery isn't free:** under the bad order, 6 transfers gave up after 5 resubmits. Nothing they did remains: the invariants hold, and their keys have no rows. A real client would keep retrying with the same key.
- **The fix removes the cause:** no deadlocks, no retries, and the round finishes about 59 times sooner.
- **The oversized transfer** was refused in every round with `23514`, and never retried.
- **The log undercounts:** `deadlocks.log` held 2,087 `deadlock detected` reports for the counter's 2,591. Use `pg_stat_database.deadlocks`.

**With PostgreSQL's default `deadlock_timeout` of 1 s,** the bad order looks different: 161 deadlocks and about 550 lock timeouts per round, and only 174 of 500 transfers committed. Lock waits time out before the deadlock check runs, so deadlocks hide as `55P03`, which isn't retried. That is the failure D1 sets 100 ms to prevent.

## Rerun under D54 (review finding L2)

After [D54](../../docs/decisions.md#d54) removed the runner's 2 s override, the lab runs D1's full pair: `lock_timeout` 200 ms and `deadlock_timeout` 100 ms. Same 5 rounds per mode:

| Mode | Wall (median) | p99 | Deadlocks (log) | `40P01` retries | `55P03` per round | Committed | Invariants |
|---|---:|---:|---:|---:|---:|---:|---|
| unordered | 4,145 ms | 4,132 ms | 185 | 185 | about 540 | 149 of 500 | all hold |
| ordered | 1,332 ms | 1,320 ms | 0 | 0 | 0 | 500 of 500 | all hold |

- **Deadlocks are still detected and retried:** each of the 185 `deadlock detected` reports in the log has its `40P01` retry. `pg_stat_database.deadlocks` read 148 at the end, because the statistics flush late, so here the log is the complete count.
- **Under the bad order, a hot pair of rows now fails fast:** 100 writers queueing on two rows wait longer than 200 ms, so most give up with `55P03`. That error isn't retried; the client gets 503 and resubmits. The round ends in about 4 s instead of 55 s, with 149 of 500 committed, and nothing they did half-way remains. That is D1's design: shed load rather than hold connections.
- **The fix still removes the cause:** with ordered locks, every transfer commits, with no deadlock and no lock timeout.

## Found while building it

`TxRunner` sets `lock_timeout` to 2 s inside every transaction, `set_config('lock_timeout', '2s', true)`, which overrides D1's role setting of 200 ms. `check-db-settings.sh` reads `lock_timeout` outside a transaction, so it doesn't see this. It was raised as [E9](../../docs/gates/stage-1-escalations.md#e9-txrunner-overrides-the-d1-lock_timeout) and removed under [D54](../../docs/decisions.md#d54) in `0056f0d`. The results above predate that fix: rerun the lab before comparing lock-timeout counts with them.

## Limits

A laptop preview with the generator and the database on one machine. Two hot rows and 100 concurrent writers are a worst case, not the payment path, which has no balance rows.
