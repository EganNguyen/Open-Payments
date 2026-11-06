# Console index comparison

Task C2 Step 1: the Stage 1 workload with and without the four console indexes from Task C4.

Status: **laptop preview, stopped early by the owner** · Run date: 2026-11-06 · Commit: `afb924d`

**Verdict: no cost was detected, and `refunds_payment` is needed regardless.** This comes from three valid runs, one of them without the indexes, so it can show a large cost but can't rule out a small one. Run 4 is invalid, because the owner stopped it during its correctness checks.

## Setup

| Item | Value |
|---|---|
| Machine | MacBook, 18 cores, 48 GB, with all services and the load generator on it |
| Workload | Step 4: 250 req/s for 600 s, [D47](../docs/decisions.md#d47) mix, 200 ms mock PSP, `--stage phase1` |
| Replication | Quorum, 2 standbys |
| Order | Without, with, with, without, so linear drift from database growth cancels |
| Before each run | Drop or create the indexes, `vacuum analyze`, `checkpoint`, wait 60 s |

Indexes tested: `payments_review`, `payment_attempts_review`, `refunds_review` and `refunds_payment`, as defined in Task C4.

## Results

| Run | Indexes | Admitted req/s | Unexpected errors | Platform p50 | p95 | p99 | WAL | Run valid |
|---|---|---|---:|---:|---:|---:|---:|---|
| 1 `20261106T080325Z-afb924d-s4-03af` | Without | 246.16 | 0% | 7.87 ms | 12.38 ms | 16.70 ms | 585.6 MB | Yes |
| 2 `20261106T081809Z-afb924d-s4-6992` | With | 246.18 | 0% | 7.02 ms | 12.05 ms | 17.10 ms | 643.1 MB | Yes |
| 3 `20261106T084713Z-afb924d-s4-1403` | With | 246.07 | 0% | 6.76 ms | 10.40 ms | 15.08 ms | 695.6 MB | Yes |
| 4 `20261106T093937Z-afb924d-s4-6fbc` | Without | 246.03 | 0% in k6 | 9.73 ms* | 15.42 ms* | 19.91 ms* | Not recorded | **No: checks unfinished** |

\* Calculated for this record as k6 end-to-end latency minus the fixed 200 ms, which gives the published figures for runs 1-3. Run 4 has no `result.md`, so these numbers are indicative only.

Runs 1-3 passed their gates, with CK1-CK6, CK9 and CK10 passing and CK7-CK8 not applicable.

## What this shows

- **Throughput:** no change in the valid runs.
- **p99:** the two runs with indexes sit on either side of the one valid run without them, so no change can be separated from noise.
- **WAL:** it rose from run to run, but the database also grew. Run 4 would have separated the two, so the indexes' effect on WAL isn't established.

## What analysis shows

These don't depend on the runs above.

- **HOT updates can't be lost.** HOT updates were 0% on `payments`, `payment_attempts` and `refunds` in run 1, with none of the new indexes. Existing partial indexes already put `state` in their conditions, and `payments_unresolved` already indexes `updated_at`.
- **The three `REVIEW` indexes are cheap to write.** A partial index gets an entry only when the row matches its condition, and `REVIEW` rows should be rare. These runs had no check that any row reached `REVIEW`, so they don't measure that case. The queue query must use the literal `state = 'REVIEW'`, because a generic plan with a parameter can't use the partial index.
- **`refunds_payment` fixes a live defect.** `Sql.LoadRefund` filters `refunds` on `payment_id`, which has no index, so the plan is a sequential scan. At the time of this record, `refunds` had 1,215,455 sequential scans reading about 19.7 billion rows, on a table of 41,847 rows. See [D50](../docs/decisions.md#d50).

## Harness defect found

Check time grew much faster than the data, which is why the comparison took about 3 hours instead of the estimated 50 minutes.

| Run | Check time | Payments at the end |
|---|---:|---:|
| 1 | 3.4 min | 83,926 |
| 2 | 17.7 min | 160,069 |
| 3 | 41.0 min | 236,425 |
| 4 | Stopped after more than 67 min | 312,613 |

The query running at the stop has a correlated subquery, `select max(o.sequence) from outbox o where o.aggregate_id = p.id`, which scans `outbox` once for each payment. A set-based form of that branch, joining payments to `max(sequence)` grouped by `aggregate_id`, took 0.385 s on the same data and found 0 violations. The code isn't fixed yet, and the other checks' times haven't been measured separately. Don't add an index on `outbox` for this, because every payment write would pay for it.

## Excluded attempts

| Run | Why it doesn't count |
|---|---|
| `20261106T073019Z-f37f463-s4-3b86` | Killed: the stack was reset during the run |
| `20261106T073451Z-1dda41e-s2-1d67` | Warm-up, invalid: capture share out of limits and NUL bytes in the effect log, fixed in `095f2b2` |
