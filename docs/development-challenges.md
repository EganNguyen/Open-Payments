# Development Challenges

Engineering problems found while building and benchmarking Stage 1, why they happened, how each was fixed, and the evidence that the fix worked. Every entry cites its source: an escalation (E), a decision (D), a run ID or a commit.

Status: 23 challenges · 17 fixed and verified · 2 fixed, verification pending · 4 open · Last updated: 2026-11-09

## How each challenge is worked

| Step | What it means here |
|---|---|
| **1. Baseline** | Record throughput, p50/p95/p99, CPU, memory and error rate for the same step, mix and database size before changing anything. Runs live in `benchmarks/runs/` and [BASELINE](../benchmarks/BASELINE.md). |
| **2. Detect** | A failed gate, an invalid run, a correctness check (CK1-CK10) failure, or a number that disagrees with another source. |
| **3. Investigate** | Collect evidence: `EXPLAIN`, `pg_stat_*`, wait events, server logs, k6 summaries, Prometheus, a minimal probe or lab. Name the cause before touching code. |
| **4. Fix** | Change one thing. Record the trade-off. Anything that changes a decision, contract or gate is escalated, not decided by the builder ([delivery workflow](delivery-workflow.md)). |
| **5. Verify** | Rerun the same benchmark from the same state. Compare before and after, and confirm the correctness checks still pass. |
| **6. Prevent regression** | Add a test, a plan check, a settings check, a metric or a gate so the problem can't come back silently. |

**Trust rule:** every number below is a laptop preview (18-core MacBook, generator and database on one machine). It is good for comparing before and after, not for capacity claims ([D53](decisions.md#d53)).

## Summary

| # | Challenge | Kind | Status |
|---|---|---|---|
| [DC-01](#dc-01-commit-cancellation-could-lose-rpo-0) | Commit cancellation could lose RPO 0 | Correctness, driver | Fixed, verified |
| [DC-02](#dc-02-deadlocks-from-opposite-lock-order) | Deadlocks from opposite lock order | Concurrency | Fixed, verified (lab) |
| [DC-03](#dc-03-txrunner-silently-overrode-lock_timeout) | `TxRunner` silently overrode `lock_timeout` | Concurrency, config | Fixed, verified |
| [DC-04](#dc-04-refund-lookup-scanned-the-whole-table) | Refund lookup scanned the whole table | Slow SQL | Fixed, verified |
| [DC-05](#dc-05-correctness-checks-grew-quadratically) | Correctness checks grew quadratically | Slow SQL | Fixed, verified |
| [DC-06](#dc-06-commit-throughput-limits-in-the-database-spike) | Commit throughput limits in the database spike | Throughput | Partly fixed, SSD open |
| [DC-07](#dc-07-query-count-budgets-had-no-basis) | Query-count budgets had no basis | N+1 guard | Fixed, verified |
| [DC-08](#dc-08-the-error-rate-gate-could-never-fail) | The error-rate gate could never fail | Measurement | Fixed, verified |
| [DC-09](#dc-09-setup-traffic-polluted-p99) | `setup()` traffic polluted p99 | Measurement | Fixed, verified |
| [DC-10](#dc-10-the-mix-share-rule-failed-by-chance) | The mix share rule failed by chance | Measurement, statistics | Fixed, verified |
| [DC-11](#dc-11-completed-count-mismatch-after-a-restart) | Completed count mismatch after a restart | Measurement, procedure | Fixed, verified |
| [DC-12](#dc-12-ck1-reported-ledger-tampering-that-didnt-happen) | CK1 reported ledger tampering that didn't happen | Correctness check | Fixed, verified |
| [DC-13](#dc-13-the-ramp-never-reached-its-target) | The ramp never reached its target | Load model | Fixed, verified |
| [DC-14](#dc-14-runs-could-pass-with-checks-missing) | Runs could pass with checks missing | Measurement | Fixed, verified |
| [DC-15](#dc-15-ck2-checked-the-wrong-effect-log) | CK2 checked the wrong effect log | Correctness check | Fixed, verified |
| [DC-16](#dc-16-the-workload-mix-didnt-match-the-product) | The workload mix didn't match the product | Load model | Fixed, verified |
| [DC-17](#dc-17-k6-metric-series-grew-without-bound) | k6 metric series grew without bound | Generator memory | Fixed, verified |
| [DC-18](#dc-18-k6-dropped-arrivals-at-1000-reqs) | k6 dropped arrivals at 1,000 req/s | Generator | Fixed, **rerun pending** |
| [DC-19](#dc-19-the-c6-refund-race-test-hid-unexpected-answers) | The C6 refund race test hid unexpected answers | Test quality | Fixed, verified |
| [DC-20](#dc-20-the-console-stack-wouldnt-start-and-mfa-was-refused) | The console stack wouldn't start, and MFA was refused | Integration | Stack fixed; MFA **decided, not built** |
| [DC-21](#dc-21-no-linux-host-for-a-capacity-number) | No Linux host for a capacity number | Environment | **Open** |
| [DC-22](#dc-22-mock-psp-capacity-unproven-at-25000-reqs) | Mock PSP capacity unproven at 25,000 req/s | Environment | **Open** |
| [DC-23](#dc-23-scope-gaps-found-at-the-phase-1-gate) | Scope gaps found at the Phase 1 gate | Scope | Moved to Stage 2 |

**Patterns:**

- **9 of 23 were the measurement lying, not the system** (DC-08 to DC-17). Fixing the harness came first, because a bottleneck named from a wrong number sends the work the wrong way.
- **Correctness checks found real defects and false alarms alike.** In every false alarm the check was kept as strict and the procedure was fixed (DC-12), never the reverse.
- **Two defects were invisible to existing checks:** the `lock_timeout` override (DC-03) passed the settings check, and the error gate (DC-08) couldn't fail. Each got a test that reads the value where it is actually used.

---

## Database and concurrency

### DC-01: Commit cancellation could lose RPO 0

| | |
|---|---|
| **Detect** | Task 13 required checking Npgsql's commit-timeout behavior before building the transaction runner ([E1](gates/stage-1-escalations.md#e1-task-13-npgsql-commit-cancellation-breaks-d28)). |
| **Investigate** | A probe against PostgreSQL 18.6 with quorum replication and no standby available. `CommitAsync` with a 1 s token sent `canceling wait for synchronous replication`, and the client got a normal success for a row committed locally but **not replicated**. Closing the connection mid-commit threw `NpgsqlOperationInProgressException`. Evidence: [probe](../benchmarks/npgsql-commit-probe/README.md), [earlier server observation](../benchmarks/spike/commit-cancel.md). |
| **Root cause** | A cancel during a synchronous-replication wait makes PostgreSQL return success with only a warning. The driver reports success, so a payment can be acknowledged and lost on failover. |
| **Fix** | [D28](decisions.md#d28) amended: stop waiting instead of cancelling. `TxRunner` uses a data source with `Command Timeout=0` (checked at startup), calls `CommitAsync(CancellationToken.None)` then `WaitAsync(3 s)`, throws `CommitOutcomeUnknownException` on timeout and hands the connection to a continuation that observes the late result. Any `WARNING` notice or exception during `COMMIT` is treated as unknown. |
| **Trade-off** | An abandoned commit keeps holding its pool slot until it finishes, so a lost standby shrinks capacity rather than risking durability. The client gets 202 / unknown instead of a fast answer. |
| **Verify** | The four commit tests in Task 13 (no cancel is ever sent, a warning becomes unknown, the connection is released when a standby returns, a same-key retry during an abandoned commit returns Busy). |
| **Prevent** | Startup fails unless `Command Timeout=0`. `scripts/check-db-settings.sh` asserts PgBouncer `query_timeout = 0` and `transaction_timeout` unset. Metrics: abandoned-commit gauge, commit-warning counter. |

### DC-02: Deadlocks from opposite lock order

| | |
|---|---|
| **Detect** | A deliberate lab, to prove the runner's retry recovers money safely ([deadlock lab](../benchmarks/deadlock-lab/README.md)). |
| **Investigate** | 50 A→B and 50 B→A transfers through the production `TxRunner`. Counted `pg_stat_database.deadlocks`, `op_tx_retries_total{sqlstate}`, client resubmits and server reports. Checked money invariants every round. |
| **Root cause** | Each transfer locked its source row first, so opposite directions took the two rows in opposite orders. Plain `FOR UPDATE` still deadlocked through the `FOR KEY SHARE` locks the foreign keys take. |
| **Fix** | Lock both rows first, lowest id first, with `FOR NO KEY UPDATE`. |
| **Verify** | 5 rounds each, `deadlock_timeout` 100 ms: |

| Mode | Wall (median) | p99 | Deadlocks | Retries | Committed | Invariants |
|---|---:|---:|---:|---:|---:|---|
| Before (unordered) | 55,668 ms | 55,658 ms | 2,591 | 2,591 | 494 / 500 | hold |
| After (ordered) | 942 ms | 932 ms | 0 | 0 | 500 / 500 | hold |

About 59× faster. Retries matched deadlocks exactly, and nothing was lost or duplicated even before the fix. With PostgreSQL's default 1 s `deadlock_timeout`, deadlocks hid as 550 `55P03` lock timeouts per round, which aren't retried: that is why D1 sets 100 ms.

**Prevent:** the lab is rerunnable (`benchmarks/deadlock-lab/run.sh 5`). `pg_stat_database.deadlocks` is in every run's `resources.json`; use it, not the server log, which undercounted (2,087 against 2,591). The payment path has no balance rows ([D38](decisions.md#d38)), so this is a worst case, not today's path.

### DC-03: `TxRunner` silently overrode `lock_timeout`

| | |
|---|---|
| **Detect** | Found while building the deadlock lab: lock waits ran up to 2 s before `55P03` ([E9](gates/stage-1-escalations.md#e9-txrunner-overrides-the-d1-lock_timeout)). |
| **Investigate** | `TxRunner.cs:297` ran `set_config('lock_timeout', '2s', true)` in every transaction since `80f0e6c`, with no recorded reason. `check-db-settings.sh` read `lock_timeout` outside a transaction and saw the role's 200 ms, so it passed. |
| **Root cause** | A transaction-local override invisible to the settings check. A 2 s wait holds a connection and request slot 10× longer than designed, so a hot row fills the pool before shedding starts. |
| **Fix** | [D54](decisions.md#d54): removed the override (`0056f0d`). Under a hot row, 503 now comes after 200 ms, not 2 s. |
| **Verify** | A new test reads `current_setting('lock_timeout')` inside a runner transaction. It failed with `2s` before the fix and passes after. Building it also exposed that test fixtures gave `op_app` no `lock_timeout` at all, so the C43 tests waited on locks with no limit; the fixtures now match D1. |
| **Prevent** | The in-transaction test. Lesson: check a setting where it's used, not where it's configured. |

### DC-04: Refund lookup scanned the whole table

| | |
|---|---|
| **Detect** | During the console index comparison, `pg_stat_user_tables` for `refunds` showed 1,215,455 sequential scans reading about **19.7 billion rows** on a 41,847-row table ([comparison](../benchmarks/console-indexes.md)). |
| **Investigate** | `Sql.LoadRefund` filters on `payment_id`, which had no index. `EXPLAIN` showed a sequential scan per refund lookup. |
| **Root cause** | Missing index; the cost grows with table size, so it would have become the first bottleneck the limit search named and hidden the next real one. |
| **Fix** | [D50](decisions.md#d50): `refunds_payment` shipped in its own migration (`f400b50`), ahead of the limit search. |
| **Trade-off** | Every refund insert/update adds an index entry. |
| **Verify** | Step 4 (250 req/s, 10 min), ordered without, with, with, without: throughput 246.16 / 246.18 / 246.07 req/s, p99 16.70 / 17.10 / 15.08 ms. No cost detected in three valid runs. It can show a large cost but not rule out a small one. HOT updates were already 0%, so none could be lost. |
| **Prevent** | On the first Linux run, confirm with `EXPLAIN` that the queries use the index (instead of repeating the comparison). |

### DC-05: Correctness checks grew quadratically

| | |
|---|---|
| **Detect** | Check time across the four index-comparison runs: 3.4 → 17.7 → 41.0 → over 67 min, while payments grew only 84k → 313k. Earlier, CK4 was also cancelled by the default 30 s command timeout after the 100 req/s step. |
| **Investigate** | `pg_stat_activity` at the stop showed CK4's correlated subquery `select max(o.sequence) from outbox o where o.aggregate_id = p.id`, which scans `outbox` once per payment. |
| **Root cause** | Correlated subquery → O(payments × outbox). |
| **Fix** | `d91cca9`: join payments to one grouped pass over `outbox`. `fc84915`: checks runner uses `Command Timeout=0`. Rejected: an index on `outbox`, because every payment write would pay for it to speed up a check. |
| **Verify** | The set-based form took 0.385 s on the same data and returned the same rows (0 violations). |
| **Prevent** | A plan test rejects any `SubPlan` in CK4. Each check records `duration_ms`, and `result.md` lists every check with its time, so the next slow check is visible on the first run. |

### DC-06: Commit throughput limits in the database spike

| | |
|---|---|
| **Detect** | The pre-build database spike searched for the highest durable commit rate ([spike summary](benchmarks/spike/macbook-2026-11-05/summary.md#bottlenecks)). |
| **Investigate** | Removed one limit at a time and measured; wait events (`WALWrite`, `SyncRep`, `BufferContent`, extension locks), per-process CPU. |

| # | Bottleneck | Evidence | Fix | Result |
|---|---|---|---|---|
| 1 | Docker port forwarding | Generator inside vs outside the VM | Run natively | 2,018 → 3,319 scripts/s |
| 2 | One PgBouncer process | 0.99 core used | 4 processes | → 4,880 scripts/s |
| 3 | Pool too small | Pool 50 → 400 | Tuned per disk | 1,029 → 3,794 scripts/s (3.7×) |
| 4 | Relation-extension lock on `payments` | Hundreds of lock timeouts | Schema C ([D25](decisions.md#d25)) | 0-8 timeouts, one run |
| 5 | Hot last pages (`BufferContent`) | Up to 88% of waits | Schema C | Unmeasured with fast flushes |
| 9 | **SSD flush** | `WALWrite` + `SyncRep` 80-91%; 3.26 ms idle flush | None without weakening durability | **Open: hardware** |

**Trade-off refused:** turning off quorum replication or fsync would raise the number and is forbidden ([E4 rules](gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4)). A fast-flush run (about 9,750 commits/s) is recorded only to show the disk is the limit, never as a result.

**Still open:** non-HOT updates from `state` in partial-index predicates, `MultiXactGen` waits (up to 10%), statement round trips (pipelining +24% in one Docker run). All need Linux.

### DC-07: Query-count budgets had no basis

| | |
|---|---|
| **Detect** | Review found the S3 test's limits (create ≤ 40, read ≤ 10, capture ≤ 40, refund ≤ 50) had no design source ([E6](gates/stage-1-escalations.md#e6-s3-query-count-budgets-have-no-design-source)). |
| **Investigate** | Measured: create 29, read 4, capture 37, refund 37. A request on one payment with no related rows can't show an N+1 pattern, so a fixed ceiling alone proves nothing. |
| **Fix** | [D48](decisions.md#d48): three rules. Command count must not grow with related rows (refund with 1 vs 10 prior refunds, etc.); per-path ceilings set to the measured counts; per-transaction statement and round-trip counts recorded. |
| **Verify** | All paths measured: create 29, read 4, capture 37, refund 37, sale 29, cancel 16, void 33, attempt 34, replay 2 ([Phase 1](phase-gates/phase-1.md#checklist)). |
| **Prevent** | Lowering a ceiling is free; raising one needs a design review with the commands added. Batching is the first fix to try if Linux shows pool or lock waits. |

---

## Measurement integrity

### DC-08: The error-rate gate could never fail

| | |
|---|---|
| **Detect** | A step 0 run sent no API token, got **401 on every request**, and reported 0.000% errors with gates passed (`…040109Z-3d728c1-s0-3b3c`). |
| **Root cause** | `report.py` read k6 Rate metrics by `rate`, a key only counters have. It found nothing and defaulted to 0. The rejected rate also read the wrong metric. |
| **Fix** | `4540c23`: read Rate metrics by `value`; rejected rate from `k6_requests_rejected_total`. |
| **Verify** | Earlier recorded runs had `op_unexpected_error` value 0, so their results stand. |
| **Prevent** | `test_report.py` covers Rate parsing. Lesson: a gate needs a test that it can fail. |

### DC-09: `setup()` traffic polluted p99

| | |
|---|---|
| **Detect** | After the reset, step 0 passed p50 (19.97 ms) but failed p99 at 137.61 ms against 100 ms ([E8](gates/stage-1-escalations.md#e8-step-0-cant-pass-at-the-d47-share-rule-and-fails-its-latency-gates-at-1-reqs)). |
| **Investigate** | The six slowest requests (336 ms each) were one burst at 04:28:14: k6's `setup()` prefill against an API restarted seconds earlier. At 336 requests, 6 outliers set p99. |
| **Root cause** | k6 counts `setup()` in `http_req_duration`. A measurement defect, not load. |
| **Fix** | `3faaaf6`: `mix.js` exports `http_req_duration{scenario:mix}`; `report.py` judges latency on it. Gate values unchanged. |
| **Verify** | Same step, same state: p99 137.61 → **30.35 ms**, p50 20.41 ms (`…043519Z-3faaaf6-s0-14c1`). |
| **Prevent** | Fallback to the old metric only for older runs, so history stays comparable. |

### DC-10: The mix share rule failed by chance

| | |
|---|---|
| **Detect** | Step 0 was invalid: status share 18.1% (target 15%), refund 6.8% (target 10%). |
| **Investigate** | 309 mix requests. A 10% share has a binomial standard deviation of 1.7 points at that size, so a fair run lands more than 2 points off at least one of seven targets most of the time. |
| **Root cause** | A fixed ±2-point rule ignores sample size. |
| **Fix** | `18d0f63`, owner's E8 answer: a share is valid within `max(2 points, 3 × sqrt(p(1−p)/n))`. Rejected: rerunning until it passed (cherry-picking). |
| **Verify** | Steps 0, 1, 3, 5 valid on the reset database. From step 2 (about 15,000 requests) the 2-point floor applies as before. |
| **Prevent** | `share_limit` in `report.py` with tests. |

### DC-11: Completed count mismatch after a restart

| | |
|---|---|
| **Detect** | Step 0: metric read 188 completed payments, the database 312,801. |
| **Root cause** | `op_payments_completed_total` counts from API process start. The API was rebuilt without resetting the database left by the index comparison. |
| **Fix** | Procedure: a [full benchmark reset](../benchmarks/README.md#full-benchmark-reset) before a search (owner-approved, since it can't be undone). |
| **Verify** | Every step after the reset reconciled. |
| **Prevent** | `257b4bf`: a missing count reconciliation makes the run invalid instead of passing silently. `d66aef6` aligned the k6 and database definitions of "completed" with the glossary. |

### DC-12: CK1 reported ledger tampering that didn't happen

| | |
|---|---|
| **Detect** | (a) A second ramp step failed CK1 although rows were only appended. (b) After a reset, a valid step 2 failed CK1 with one `ledger_manifest` violation ([E7](gates/stage-1-escalations.md#e7-benchmark-reset-leaves-the-ck1-ledger-manifest-stale)). |
| **Investigate** | (a) The manifest was an aggregate, so any new row looked like a change. (b) The reset truncated the ledger (25,890 rows after) but not `ck.ledger_rows` (148,646); 122,756 manifest rows had no ledger row. |
| **Fix** | (a) `84752cd`: track each row hash; flag only changed or deleted rows. (b) `c249adc`: clear the manifest in the same reset that empties the ledger, with API and mock stopped. CK1 itself unchanged. |
| **Verify** | Rerun `…075557Z-c249adc-s2-14be` passed, including CK1. |
| **Prevent** | Regression test for appends. The manifest is never cleared alone or mid-run, so real tampering stays detectable. |

### DC-13: The ramp never reached its target

| | |
|---|---|
| **Detect** | Old-mix step 3 targeted 100 req/s and delivered 50.48 average. |
| **Root cause** | A single-stage ramp from 1 req/s touched the target only at the end. |
| **Fix** | `04b84ec`: 5 s ramp, then hold the target for the full step. |
| **Verify** | D47 step 3 admitted 99.16 req/s; step 5, 491.98; step 6, 988.84. |
| **Prevent** | `result.md` reports admitted against target req/s for every run. |

### DC-14: Runs could pass with checks missing

| | |
|---|---|
| **Detect** | Review of `report.py`: an empty checks file passed; local runs had no Prometheus, so CK6 couldn't run; a failed optional Prometheus `curl` aborted `run.sh` before `result.md` was written; CK6 failed on an in-flight gauge that starts at zero. |
| **Fix** | `6cc7d1a`: an empty check set or missing CK6 fails the gate. `257b4bf`: unchecked validity rules are shown and make the run invalid; CK6 gets the Prometheus URL and time range. `55184a8`: the optional query can't abort a run. `6e0ece1`: CK6 compares the final window with the first, so only a continuing climb fails. |
| **Verify** | D47 steps record CK1-CK6, CK9, CK10 all applicable and passing. |
| **Prevent** | Principle now in the report: "not checked" is never "passed". |

### DC-15: CK2 checked the wrong effect log

| | |
|---|---|
| **Detect** | CK2 (one PSP effect per key) saw none of the run's new attempts or refunds. |
| **Root cause** | The mock's effect log was copied before the run, not after the drain. Also NUL bytes in the log (fixed in `095f2b2`). |
| **Fix** | `34e711e` persists the log; `d407535` captures it after the drain. |
| **Verify** | CK2 passes on every D47 step with the run's own effects. |

### DC-16: The workload mix didn't match the product

| | |
|---|---|
| **Detect** | `mix.js` never sent a standalone capture, so a committed mode never ran under load ([E5](gates/stage-1-escalations.md#e5-task-17-workload-mix-has-no-agreed-saletwo-call-shares)). The target `T` ranged from 8,300 to 16,700 req/s depending on the unstated split. |
| **Fix** | [D47](decisions.md#d47): 40% sale, 10% authorize, 10% capture, 15% status, 10% refund, 10% retry, 5% other; `T` = 10,000 req/s. Captures come from a per-VU queue of authorized payments, with substitutions counted. |
| **Verify** | Step 2: sale 40.10%, authorize 10.38%, capture 9.51%, status 15.16%, refund 9.98%, retry 10.05%, other 4.83%. A test confirms a capture doesn't raise the completed count. |
| **Prevent** | Achieved shares are reported and judged on every run (DC-10). Old-mix runs are kept as comparative only. |

### DC-17: k6 metric series grew without bound

| | |
|---|---|
| **Detect** | k6 unique-series warning during D47 runs. |
| **Root cause** | High-cardinality tags (per-request values) created a new metric series per request, growing generator memory. |
| **Fix** | `095f2b2`: bounded tags. |
| **Verify** | Step 2 at the new mix ran with no unique-series warning. |

### DC-18: k6 dropped arrivals at 1,000 req/s

| | |
|---|---|
| **Detect** | Step 6 (1,000 req/s) passed every gate but was **invalid**: k6 dropped 68 of 902,428 arrivals (0.0075%). |
| **Investigate** | Generator CPU 1.9%, so not CPU. k6 used at most 305 of 800 VUs; the drops happened while it started VUs past the 300 pre-allocated. |
| **Root cause** | Starting VUs mid-run on this laptop. A generator setup limit, so no system bottleneck was named. |
| **Fix** | `7dd42a7`: `preAllocatedVUs = maxVUs`, so all VUs start before the load. |
| **Verify** | **Pending:** step 6 must be rerun from a reset. [D51](decisions.md#d51)'s laptop milestone (500 completed payments/s) needs three valid 10-minute windows; step 6 reached 498.33/s with p99 58.43 ms in its one invalid run. |
| **Prevent** | Dropped arrivals already invalidate a run. |

### DC-19: The C6 refund race test hid unexpected answers

| | |
|---|---|
| **Detect** | Review of the 50-way concurrent refund test. |
| **Root cause** | Any non-success status counted as a rejection, so a 500 would have passed. |
| **Fix** | `e687b3c`: retry only the documented 409 with `Retry-After`; fail on any other status. Still requires exactly 10 successes and 40 rejections. |
| **Verify** | Test passes with the stricter rule. |

---

## Integration and environment

### DC-20: The console stack wouldn't start, and MFA was refused

| | |
|---|---|
| **Detect** | Running the compose stack for console Task C13. |
| **Investigate and fix** (`87257fd`) | API had no healthcheck → wait for start. Keycloak needed a Host header and dynamic back-channel URLs to serve keys. The aspnet image's `sh` has no `/dev/tcp`. Keycloak 26 required names on the realm user. `Secure`-only cookies broke every form over local http → Development follows the request scheme, like Testing. |
| **Then** | Sign-in with password + TOTP showed "No access" ([E10](gates/stage-1-escalations.md#e10-local-keycloak-sign-in-carries-no-mfa-claim-so-the-console-refuses-it)). `kcadm` showed the OTP authenticator `configurable: false`, so Keycloak 26.0's `amr` mapper had nothing to emit. The console failed closed, as designed. |
| **Fix decided** | [D55](decisions.md#d55): accept MFA from `acr` (level-of-authentication flow, TOTP = level 2) as well as `amr`. Rejected: lowering `RequiredAmr` locally, which removes MFA. |
| **Status** | **Not built yet:** no `RequiredAcr` in `src/`. Verify by signing in through local Keycloak with TOTP and reaching the console, plus a test that a password-only session is refused. |

### DC-21: No Linux host for a capacity number

| | |
|---|---|
| **Problem** | Capacity `C` and cells `N` need benchmark-class Linux: separate generator, NVMe with WAL on its own volume, quorum across zones, 100M prefilled payments ([E4](gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4)). Only the MacBook exists. |
| **Decision** | [D53](decisions.md#d53): Stage 1 closes on laptop evidence through step 6. `C`, `N` and the mock's 25,000 req/s check move to the first Linux run, a required step in Stage 4. No capacity number is claimed. |
| **Laptop result** | Steps 0-6, 0.000% unexpected errors, pool wait 0, deadlocks 0, p99 ≤ 58.43 ms. API CPU grew to about 1.9 cores at step 6 while database CPU stayed under half a core; no bottleneck named yet. |

### DC-22: Mock PSP capacity unproven at 25,000 req/s

| | |
|---|---|
| **Problem** | The mock must add under 5 ms p99 at 25,000 req/s, or it becomes the bottleneck being measured. |
| **Evidence** | 10,000 req/s is a valid laptop preview at 1.34 ms added p99; 25,000 is invalid on this laptop ([mock capacity](../benchmarks/mock-psp-capacity.md)). Unproven, not disproven. |
| **Status** | Open, with DC-21. `MOCK_ADDED_P99_MS` is set by hand until then. |

### DC-23: Scope gaps found at the Phase 1 gate

| | |
|---|---|
| **Problem** | Two edge-case tests had no code to exercise: a late `declined` callback (no callback route exists) and idempotency-key expiry (no expiry job) ([E2, E3](gates/stage-1-escalations.md#e2-task-16-late-declined-callback-has-no-receiver)). |
| **Decision** | [D46](decisions.md#d46): both move to Stage 2's C31b. A domain-only test that no request can reach was rejected as fake coverage. Until expiry exists, keys are kept, which is the safe direction: a late retry replays and can't charge twice. |
| **Consequence** | `idempotency_keys` only grows during Stage 1 runs. V7 (batched delete under load) must be measured at Stage 2's design gate. |

---

## Open work

| Item | Next step | Done when |
|---|---|---|
| DC-18 step 6 rerun | Full reset, rerun step 6 three times with pre-started VUs | Three valid 10-min windows at ≥ 500 completed payments/s, all checks pass |
| DC-20 MFA by `acr` | Build D55 in the console and the local realm | TOTP sign-in reaches the console; password-only is refused |
| DC-21 / DC-22 Linux | Provision a host meeting E4's list | Mock at 25,000 req/s under 5 ms; steps 4-5 give `C` and `N` |
| DC-04 on Linux | `EXPLAIN` the refund and review queries | Plans use the indexes |
| DC-06 open bottlenecks | Linux spike: non-HOT updates, `MultiXactGen`, round trips | Each measured, fixed or ruled out one at a time |

## Regression guards added

| Guard | Catches | From |
|---|---|---|
| Startup check `Command Timeout=0`; settings check for `query_timeout`, `transaction_timeout` | A commit cancel returning | DC-01 |
| Test reading `lock_timeout` inside a runner transaction | Hidden per-transaction overrides | DC-03 |
| CK4 plan test rejecting `SubPlan`; per-check `duration_ms` | Quadratic checks | DC-05 |
| S3 no-growth tests and per-path ceilings | N+1 queries, statement creep | DC-07 |
| `test_report.py`: Rate parsing, `share_limit`, scenario latency | Gates that can't fail or fail by chance | DC-08 to DC-10 |
| Run invalid on unchecked rules, missing checks, count mismatch, dropped arrivals | Vacuous passes | DC-11, DC-14, DC-18 |
| CK1 per-row hash test; reset clears manifest with the ledger | False tampering, hidden tampering | DC-12 |
| Deadlock lab and `pg_stat_database.deadlocks` in every run | Lock-order regressions | DC-02 |
| Abandoned-commit gauge, commit-warning counter, `op_tx_retries_total` | Durability and retry pressure in production | DC-01, DC-02 |
