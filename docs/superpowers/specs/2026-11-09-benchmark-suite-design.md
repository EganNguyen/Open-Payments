# OP-BENCH: End-to-End Benchmark and Reliability Suite

A review of the proposed OP-BENCH v1 specification against this repository, and the design that replaces it. The suite is C# (.NET 10) apart from k6, which stays as the throughput generator.

Status: `Committed`, approved by the owner with five refinements on 2026-11-09 ([D51](../../decisions.md#d51), [D52](../../decisions.md#d52)). The design is frozen; changes go through the [gate record](../../gates/op-bench-design.md) · Last updated: 2026-11-09 · Build plan: [2026-11-09-benchmark-suite.md](../plans/2026-11-09-benchmark-suite.md)

**The question the suite answers:** how many completed payments per second does Open Payments sustain through its real HTTP API, with every money check passing, latency inside its SLO, and recovery working when things fail?

## 1. Review of the proposed specification

The proposal is sound in its principles: real path end to end, independent reconciliation, hard gates instead of a score, `null` as "not measured", and "no PASS with missing evidence". Most of what it asks for already exists here. The rest needs correcting before it's built, because several points contradict committed decisions or would produce a number that can't be trusted.

### What already exists and must be reused

| Proposal asks for | Already in the repository |
|---|---|
| Go app, TxRunner, append-only ledger | C# API (`src/OpenPayments.Api`), [`TxRunner`](../../../src/OpenPayments.Transitions/TxRunner.cs), ledger with CK1 tamper manifest |
| k6 scenarios | [`benchmarks/k6/mix.js`](../../../benchmarks/k6/mix.js): open-model D47 mix, idempotent retries, refund targets |
| Step matrix | [`benchmarks/profiles/steps.json`](../../../benchmarks/profiles/steps.json), steps 0-11 |
| Run validity rules | [`report.py`](../../../benchmarks/report.py) + `test_report.py`: dropped arrivals, generator CPU ≤ 80%, mock p99 ≤ 5 ms, share drift ≤ 2 pp |
| Independent reconciliation | `op-checks` CK1-CK10, each proven to fail on corruption and on zero rows |
| Mock PSP effect log | `effects.csv`, compared by CK2 (exactly one effect per PSP key) |
| Metrics | [`docs/metrics.md`](../../metrics.md) contract, Prometheus, Grafana, `resources.py` |
| Deadlock retry tests | `TxRunnerTests`: 40P01 retried at most twice, 55P03 and unique violations not retried |
| Commit-ack-lost behaviour | [D28](../../decisions.md#d28) and C43 tests |
| Native durable PostgreSQL | `scripts/native-stack.sh`, `deploy/native/native.conf` (`fsync_writethrough`, quorum of 2 standbys) |

### Findings

| # | Severity | Finding | Correction |
|---|---|---|---|
| R1 | Critical | **The latency gates are unreachable.** p95 ≤ 200 ms end to end is impossible with the mock PSP at 200 ms fixed latency. The repository's SLO is on platform overhead (end-to-end minus PSP wait): p50 ≤ 25 ms, p99 ≤ 100 ms. | Gate on `op_platform_overhead_seconds`, cross-checked by k6 end-to-end minus the configured PSP latency. Report end-to-end separately, ungated. |
| R2 | Critical | **The proposal leaves out the PSP.** A payment here is transaction A (intent + attempt), the PSP call, then transaction B (result + ledger + outbox). "API → TxRunner → PostgreSQL" skips the step that makes duplicates and unknowns possible. | The mock PSP is part of every scenario, and its effect log is evidence (CK2). The mock's own added p99 must be measured per run (open item E4). |
| R3 | High | **"500 committed payments/s" is the wrong unit, and the wrong number.** The glossary defines *completed payments/s*, which includes declines and excludes captures, retries and `UNKNOWN`. The project target is 5,000. At the D47 mix half of all requests start a payment, so 500 payments/s is ~1,000 req/s, which is **step 6**, not step 5. | Use "completed payments/s" as the glossary defines it. Treat 500 as a *laptop milestone*, approved as O1, mapped to step 6. |
| R4 | High | **The 80/10/5/5 workload replaces the committed D47 mix** (40 sale, 10 authorize, 10 capture, 15 status, 10 refund, 10 retry, 5 other). Changing it invalidates every earlier run and the capacity model, which depends on `C` and `T` coming from one mix. | Throughput scenarios use D47 unchanged. Concurrent duplicates get their own scenario (C5), not a share of the mix. |
| R5 | High | **A MacBook result can't be PASS under the existing trust rules.** Only a Linux run from Phase 3 on, with all checks applicable, is a *Recorded* result. | The suite reports a verdict *and* a label: `PREVIEW` on the laptop, `RECORDED` only where the existing rules allow. A laptop can give `PASS (PREVIEW)`, never `PASS (RECORDED)`. |
| R6 | High | **Crash recovery can't pass yet.** "Incomplete work is safely reconciled" needs the reconciler, which is Stage 2 work. C4 and C3 can't exercise real behaviour today. | Until Stage 2, crash scenarios check what is buildable now (no acknowledged answer lost, no second PSP effect, every interrupted attempt findable) and report the reconciliation part as `NOT_APPLICABLE`, which keeps the suite `INCONCLUSIVE` for that gate. |
| R7 | Medium | **A two-account transfer fixture proves the retry loop, not the product.** The product's real deadlock risk is several writers on one `payments` row (capture, refund, reconciler), which is C34. | Keep the fixture, in its own `txlab` schema, as the TxRunner lab. Add the real-path C34 race through the HTTP API as the product evidence. |
| R8 | Medium | **"Lock wait p50/p95/p99" has no source today.** `pg_locks_count` counts locks held, not waits, and PostgreSQL keeps no lock-wait histogram. | Sample `pg_stat_activity` for `wait_event_type = 'Lock'` every 100 ms, plus `log_lock_waits` lines. Report it as a sampled estimate, labelled so. |
| R9 | Medium | **Retry exhaustion can't be forced with real deadlocks.** TxRunner retries 40001/40P01 at most twice, and real cycles rarely recur three times in a row. | The lab raises a real `PostgresException` with SQLSTATE 40P01 from PL/pgSQL on every attempt (the method `TxRunnerTests` already uses). That proves the bound and the `retry_exhausted` 503 without relying on timing. |
| R10 | Medium | **"Survives a database restart" needs a client-side record of what was acknowledged.** Reconciliation alone can't tell a lost commit from one that never happened. | A C# fault driver keeps an append-only *ack log* (key, status, payment ID, outcome) and a new check proves every acknowledged answer exists in the database with the same outcome. |
| R11 | Low | Docker durability isn't equivalent to native (Docker flush ~60 µs, native ~3.26 ms). | Throughput scenarios run native only. Docker is for correctness and fault tests, where flush speed doesn't decide the result. |
| R12 | Low | `pg_stat_statements` isn't enabled in any PostgreSQL config. `log_lock_waits` is on in the Docker `primary.conf` and already checked by `check-db-settings.sh`, but not in `native.conf` or `primary.macbook.conf`. | Enable `pg_stat_statements` everywhere and `log_lock_waits` in the two configs that lack it. Preflight verifies both (§4.1). |
| R13 | Low | Grafana/OpenTelemetry are optional evidence. A dashboard isn't a gate ([metrics](../../metrics.md#alerts-and-gates)). | Prometheus queries feed the report. No OpenTelemetry tracing is added for this suite. |

## 2. Principles

1. **Reuse, don't fork.** k6 `mix.js`, `run.sh`, `report.py`, `op-checks`, the mock PSP and the metric contract stay. New code adds only what is missing.
2. **Each number has one source of truth**, named in the JSON report next to the value.
3. **Correctness first.** A correctness failure fails the suite, however fast.
4. **Missing is not zero.** An unmeasured value is `null` and makes its gate `INCONCLUSIVE`.
5. **A broken generator is not a broken system.** If the load generator saturates or drops arrivals, the run is invalid, not slow.
6. **Fault tests prove the fault happened.** A restart scenario with no observed connection loss proves nothing, so it is `INCONCLUSIVE`.

## 3. Architecture

```
                     op-bench (C#, tools/OpenPayments.Bench)
                     preflight · orchestrate · sample · verify · report
                       |                |                    |
        throughput     |   faults       |       evidence     |
        k6 mix.js      |   C# FaultDriver (ack log)          |  Prometheus, pg_stat_*,
        via run.sh     |   Toxiproxy, pg_ctl, kill -9        |  lock sampler, effects.csv
                       v                v                    v
   +-----------------------------------------------------------------------+
   |  Open Payments API  ->  TxRunner  ->  PgBouncer  ->  PostgreSQL 18     |
   |        |                                             primary + 2 sync  |
   |        +----> Mock PSP (effect log)                  standbys (quorum) |
   +-----------------------------------------------------------------------+
                       |
                       v
       op-checks CK1-CK11 (independent SQL, separate code from Transitions)
                       |
                       v
       reports/<suite-id>/suite.json + suite.md  ->  PASS / FAIL / INCONCLUSIVE
                                                     label PREVIEW | RECORDED
```

**Who generates load:**

| Generator | Used for | Why |
|---|---|---|
| k6 `mix.js` (existing) | Steps, target, stress, spike, soak | Open model, already validated by `test_report.py`, low overhead per request |
| C# `FaultDriver` (new) | Fault, idempotency and durability scenarios, at ≤ 250 req/s | Needs a per-request ack log, response-loss control and exact timestamps that k6 can't record cheaply |

The fault driver never produces a throughput number. Its rate is capped so it can't become the bottleneck.

## 4. Components

### 4.1 `tools/OpenPayments.Bench` (new): the orchestrator, `op-bench`

A console app, referencing nothing in `OpenPayments.Transitions` so it stays independent ([D23](../../decisions.md#d23)).

| Command | Does |
|---|---|
| `op-bench preflight` | Clean git tree; native stack up; `check-db-settings.sh` passes; quorum standbys healthy; Prometheus targets up; mock PSP capacity record present; disk headroom; `pg_stat_statements` checks (below); writes the reproducibility manifest (§8.1) |
| `op-bench run <suite>` | Runs a suite (`smoke`, `correctness`, `reliability`, `performance`, `full`) scenario by scenario, snapshots evidence before and after each, never resets statistics without exporting them first |
| `op-bench verify <suite-id>` | Re-runs evidence completeness and gate evaluation on a stored suite, so a report can be checked by someone else |
| `op-bench report <suite-id>` | Writes `suite.json` and `suite.md` |

**`pg_stat_statements` preflight.** On the primary: `pg_stat_statements` is in `shared_preload_libraries`; `create extension if not exists pg_stat_statements` has run in `openpayments`; the benchmark role can read every row (`pg_read_all_stats`, so `query` isn't `<insufficient privilege>`); `pg_stat_statements.track = top`. Any failure stops a performance suite before load starts. For correctness and reliability suites it marks the diagnostics section `INCOMPLETE`, which makes the suite `INCONCLUSIVE`. Statistics are exported before and after each scenario and never reset without an export first.

Throughput scenarios shell out to `benchmarks/run.sh`, so the existing validity rules apply unchanged. The k6 harness and `mix.js` are invoked, never edited, by this suite ([D51](../../decisions.md#d51)). The orchestrator reads each run directory as evidence; it doesn't rewrite them.

Project layout:

```
tools/OpenPayments.Bench/
  Program.cs                 command parsing
  Suites/                    suite definitions (which scenarios, in what order)
  Scenarios/                 one class per scenario: Arrange, Inject, Observe, Verify
  Faults/                    ToxiproxyClient, PgControl (pg_ctl), ProcessKiller
  FaultDriver/               open-model HTTP driver with the ack log
  Evidence/                  PgSnapshot, LockSampler, PromQuery, ResourceSampler
  Gates/                     thresholds.json loader, gate evaluation, verdict
  Report/                    SuiteReport (JSON contract), Markdown writer
benchmarks/config/thresholds.json
```

### 4.2 `FaultDriver`: open-model HTTP client with an ack log

- Arrivals are scheduled on a timer at a fixed rate, independent of response time, and a late or dropped arrival is counted (the same validity rule as k6).
- Every request appends one line to `acks.ndjson` *after* the response is read: `{seq, key, route, sent_at, done_at, status, payment_id, outcome, error}`. The file is flushed per batch, and a line is written for timeouts and resets too (`status: null, error: "reset"`).
- Idempotency keys are deterministic from `(suite-id, scenario, seq)`, so a retry after a fault reuses the original key exactly.
- `--lose-response`: reads only the status line, then aborts the socket. The server committed; the client never saw the body. The driver then retries the key.

### 4.3 `op-checks` CK11 (new check): client-observed outcome reconciliation

CK11 checks the client's view against the payment state machine, not HTTP codes against row counts ([D52](../../decisions.md#d52)). It reads `acks.ndjson` (`--acks <path>`), the database and the mock's effect log.

| # | Rule | Fails when |
|---|---|---|
| 1 | Every acknowledged final answer (201/200 with a final state, 402 decline) has its payment, with the same payment ID | The payment is missing, or has a different ID |
| 2 | The persisted state is one the acknowledged answer allows | Client saw `CAPTURED`, database says `FAILED`; client saw a decline, database says `CAPTURED` |
| 3 | A 202 or `UNKNOWN` answer maps to a payment whose attempt is unresolved, or resolved later by a legal transition | The attempt is missing, or reached a state no transition allows |
| 4 | A request with no answer (timeout, reset, crash) is *classified*, never assumed absent (§5.3) | An unanswered key has rows but no classification |
| 5 | Every replay of a key returned the original payment ID, and the key appears once in `idempotency_keys` | A replay got a different ID, or a key has two rows |
| 6 | Every unresolved attempt is listed with its case, and counted | An unresolved attempt is missing from the list |

**A client-observed failure may have a database record.** A decline, a fraud block or a commit-unknown 503 after transaction A can legitimately leave a `FAILED` or `UNKNOWN` row. CK11 checks that the row's state is legal for what the client saw, not that it is absent.

**PSP side effects stay in CK2.** "Every PSP effect has its expected record", "no unexpected PSP charge" and "a replay creates no second effect" are already CK2's rules: one effect per PSP key, every `SUCCEEDED` attempt and refund has its effect, and every effect has its attempt or refund ([`ck2.sql`](../../../tools/OpenPayments.Checks/sql/ck2.sql)). CK11 doesn't repeat them; the suite requires CK2 and CK11 together wherever a fault is injected.

**Unknown outcomes.** Until Stage 2's resolver exists, rule 6 *lists* unresolved attempts with their case and evidence. It doesn't claim they were reconciled, and the scenario reports resolution as `NOT_APPLICABLE` ([D52](../../decisions.md#d52)).

Like every CK, CK11 must fail on injected corruption (delete an acknowledged payment, change a state, duplicate a key row, drop a classification) and on zero rows.

### 4.4 TxRunner instrumentation (small change to `TxRunner.cs` and `Metrics.cs`)

Added to `TransitionMetrics` and exported through `/metrics`, following the contract's naming and cardinality rules. Only `reason` and `sqlstate` labels, both bounded.

| Metric | Type | Labels | Meaning |
|---|---|---|---|
| `op_tx_attempts_total` | counter | none | Every transaction attempt, first or retry |
| `op_tx_retries_total` | counter | `sqlstate` | Exists today |
| `op_tx_failed_total` | counter | `reason`: `retry_exhausted`, `lock_timeout`, `pool_wait`, `commit_unknown`, `other` | Transactions that ended without a commit |
| `op_tx_duration_seconds` | histogram | none | Begin to commit acknowledged, per attempt |
| `op_db_pool_acquire_seconds` | histogram | none | `OpenConnectionAsync` wait |
| `op_tx_retries_per_transaction` | histogram | none | 0, 1, 2 retries, so the distribution is visible |

Deadlock *count* is still read from `pg_stat_database.deadlocks`, never from these counters. The two must agree within the run: each 40P01 the server raised is one retry or one exhaustion in the app. A mismatch fails the run's evidence gate.

Connections go through PgBouncer, so `NpgsqlDataSource` pool statistics describe the client pool only. Server-side waits come from `pgbouncer_pools_client_maxwait_seconds`, as today.

### 4.5 Evidence samplers

| Sampler | Source | Interval | Writes |
|---|---|---|---|
| `PgSnapshot` | `pg_stat_database`, `pg_stat_wal`, `pg_stat_checkpointer`, `pg_stat_statements` (top 50 by total time), `pg_settings` checksum | Before and after each scenario | `pg-before.json`, `pg-after.json`, deltas |
| `LockSampler` | `pg_stat_activity` where `wait_event_type = 'Lock'`, plus `pg_blocking_pids` | 100 ms | Sampled lock-wait seconds, max wait, top blocking query pairs |
| `ResourceSampler` | `ps` for k6, API, mock, `postgres` processes; `vm_stat`; `iostat` | 1 s | CPU and RSS per process, for generator-saturation and soak-growth checks |
| `PromQuery` | Prometheus `/api/v1/query_range` | Per scenario window | All `op_` series plus exporter series |

### 4.6 Reliability tests: `tests/OpenPayments.Reliability.Tests` (new xUnit project)

Run by `dotnet test --filter Category=Reliability` against the Docker stack. Each test outputs a small JSON result the orchestrator collects.

**TxRunner lab (`txlab` schema, test fixture only).** Created and dropped by the fixture, never in `db/migrations`. Two rows in `txlab.accounts`. Nothing here enters the product. D38 and the append-only ledger are untouched.

| Test | Method | Pass |
|---|---|---|
| `Lab_opposing_transfers_deadlock_and_recover` | 100 pairs of opposite-order updates through the real `TxRunner`, released by a barrier | `pg_stat_database.deadlocks` rose; every one of the 200 transactions committed; no row lost an update; retries ≤ 2 per transaction |
| `Lab_retry_exhaustion_fails_explicitly` | Work raises SQLSTATE 40P01 every attempt | Exactly 3 attempts; `BusyException("retry_exhausted")`; nothing committed; `op_tx_failed_total{reason="retry_exhausted"}` +1 |
| `Lab_serialization_failure_is_retried` | Two `SERIALIZABLE` transactions with a write skew | 40001 retried and counted separately from 40P01 |
| `Lab_lock_timeout_is_not_retried` | A third session holds the row past `lock_timeout` (200 ms under [D54](../../decisions.md#d54); 2 s when this spec was written) | One attempt; `BusyException("lock_timeout")`; 55P03 counted |
| `Lab_pool_exhaustion_is_bounded` | Pool of 4, 50 concurrent holders | Waits are bounded by the pool timeout; failures are `pool_wait`, never a hang; recovery without a restart (C2) |

**Product races, through the HTTP API.** These are not a second deadlock test. A same-key or same-payment race may never raise 40P01: it usually ends in a unique-constraint conflict or a conditional update that changes no row. So the lab proves TxRunner recovers from a real PostgreSQL deadlock, and these prove idempotency and lifecycle concurrency. The two are reported separately and neither substitutes for the other. C34 reports how many 40P01 it actually saw; zero is allowed for C34 and is reported as such, while TX1 must see more than zero.

| Test | Existing ID | Pass |
|---|---|---|
| Capture and refunds race on one payment (new test) | C34 (reconciler part is `NOT_APPLICABLE` until Stage 2) | Any 40P01 retried at most twice; exactly one PSP effect per operation; CK1, CK2, CK3 pass |
| 100 concurrent requests with one key (exists: `CreateTests`) | C5 | One payment, one PSP charge, identical body to all 100 |
| 50 concurrent refunds over the captured amount (exists: `RefundTests`) | C6 | Refunded + pending ≤ captured |
| Two captures with different keys (exists: `CaptureTests`) | C36 | One PSP capture; the other 409 or current status |
| Invalid input (bad currency, negative amount, wrong route for key) | C31a cases | 4xx; zero rows written to any money table |

### 4.7 Fault injection

| Fault | Tool | Where |
|---|---|---|
| Latency, bandwidth limit, connection reset, black hole between API and PgBouncer | Toxiproxy (new compose service, pinned version) | Docker stack, and native stack with Toxiproxy run as a local binary |
| Response lost after commit | `FaultDriver --lose-response`, plus a Toxiproxy `limit_data` toxic on the client → API proxy | Both |
| PostgreSQL restart | `pg_ctl restart -m fast`, and `-m immediate` for crash semantics | Native |
| API crash | `kill -9` on the API process at a chosen point | Both |
| Pool exhaustion | Lab test, and an API run with a 4-connection pool | Docker |

## 5. Scenario catalogue

Each scenario is mapped to its existing ID where one exists, so results land in the records that already expect them. New IDs were approved under O4.

### 5.1 Performance (k6, native stack)

| Scenario | Steps / ID | Duration | Measures |
|---|---|---|---|
| Warm-up | not judged | 2 min | Excluded from every window |
| Baseline | Steps 0-1 | 5 min each | Latency floor, correctness at low rate |
| Load | Steps 2-5 | 5-10 min | Throughput and latency per step |
| **Target** | Step 6 (1,000 req/s ≈ 500 completed payments/s) | 10 min measured, ×3 from a reset | The acceptance number for the 500 completed payments/s milestone (O1) |
| Stress | L6 shape: ramp to 2× the last passing step | 15 min | First resource to fail, recorded |
| Spike | L3 shape at laptop scale: 3× the target for 60 s | 5 min | Time until platform p99 returns under the SLO, measured in 5 s buckets |
| Soak | L5 shape at the target rate | 60 min | RSS, Gen 2 GC/s, ThreadPool queue, pool wait slope (Ck6Trend) |

The 24 h L2 soak stays a Linux, Phase 4 item. It's not part of this suite.

**Target-run rules.** The measured window starts after the ramp reaches the rate and excludes `setup()` traffic (fixed in `3faaaf6`). The three repeats each start from the full reset in the [benchmark README](../../../benchmarks/README.md#full-benchmark-reset). The suite reports the minimum of the three as the result, with the spread.

### 5.2 Reliability (C# fault driver and tests)

Every fault scenario records: injection time, first failed request, first successful request after the fault is removed, requests affected, completed payments before and after, duplicates, missing records, retries and exhaustions.

| Scenario | ID | Rate | Pass |
|---|---|---|---|
| Forced deadlock, lab | new: TX1 | n/a | §4.6 lab tests |
| Retry exhaustion, lab | new: TX2 | n/a | §4.6 |
| Deadlock on the real path | C34 | n/a | §4.6 |
| Concurrent duplicates | C5 | 100 concurrent | One payment, one charge |
| **Response lost after commit** | new: C52 | 50 req/s, 200 losses | Every retry returns the original payment ID and outcome; CK2, CK11 pass |
| Commit acknowledgement lost, API ↔ DB | C43 (exists) plus a Toxiproxy reset during `COMMIT` | 50 req/s | Transaction A unknown → 503, no PSP call; transaction B unknown → 202, attempt findable; retries settle with no second effect |
| Network latency, 200 ms API ↔ DB | new: C53 | Step 4 | Bounded queues, overload shed as 429/503, no duplicate, SLO back within 2 min after removal |
| PostgreSQL restart under load | new: C54 (local form of C7) | Step 4 | Every acknowledged payment present after restart (CK11); failures during the outage are 503, not 500; first success timed |
| API `kill -9` under load, cases L1 and L2 | C4, partial until Stage 2 | Step 4 | No acknowledged answer lost (CK11); no second PSP effect (CK2); every interrupted payment classified (§5.3). Resolution: `NOT_APPLICABLE` |
| PSP times out but completes, case L4 | C3, partial until Stage 2 | 50 req/s | Payment `UNKNOWN` and findable; one effect. Resolution: `NOT_APPLICABLE` |
| Retry while original in flight, case L5 | new: C55 | 50 req/s | Retry renders current state; one attempt; one effect |
| Pool exhaustion | C2 | Burst | Bounded waits, controlled failures, recovery without restart |
| Invalid input | C31a subset | n/a | Rejected, no money-table mutation |

**Recovery time** is measured four ways, from the ack log timestamps, not from the server, so a slow detection or restart can't hide inside one number:

| Measure | From | To | Target |
|---|---|---|---|
| `first_success_from_injection_ms` | Fault injected | First final answer whose payment's final commit came after the fault | Reported |
| `first_success_from_removal_ms` | Fault removed | Same | ≤ 30 s |
| `slo_from_injection_ms` | Fault injected | First 5 s bucket where platform p99 ≤ 100 ms, held for 30 s | Reported |
| `slo_from_removal_ms` | Fault removed | Same | ≤ 2 min |

For a restart the "removal" is the moment `pg_ctl` reports the server ready. These are benchmark acceptance targets, not a production RTO.

### 5.3 PSP lifecycle cases

A payment is transaction A, then the PSP call, then transaction B. The fault scenarios must show *which* case each interrupted payment fell into, because each case has a different correct result. The suite doesn't rely on killing at a precise instant. It injects the fault, then **classifies every unanswered or interrupted key afterwards** from three independent records: the ack log, the database, and the mock's effect log.

| Case | How it is produced | Evidence that identifies it | Correct result |
|---|---|---|---|
| L0 Never started | Any crash or reset before transaction A commits | No key row, no attempt, no effect, no answer | Nothing to reconcile; a retry starts the payment fresh. Counted, so a run can't hide lost requests |
| L1 Crash before the PSP charge | Mock `LatencyMs` 2,000 (under the 2.5 s PSP deadline), `kill -9` the API during the wait | Attempt row from A, no effect in `effects.csv`, no answer | Attempt findable and unresolved; no effect; a retry with the same key never charges twice (CK2) |
| L2 Crash after the PSP charge, before B | Mock `ApproveThenHangRate` 1.0 (effect written, then the response hangs), `kill -9` during the hang | Attempt row, effect present, no B rows (no ledger transaction) | Attempt unresolved with its effect on record; the resolver's job in Stage 2, so resolution is `NOT_APPLICABLE` |
| L3 B commits, response lost | `FaultDriver --lose-response` (C52) | B rows present, ack line with status but no body | Retry returns the original payment ID and outcome; no second effect |
| L4 PSP times out but completes | Mock `LateLandingRate`, or `ApproveThenHang` without a kill | 202 / `UNKNOWN` answer, effect present | Payment `UNKNOWN`, findable, one effect; resolution `NOT_APPLICABLE` until Stage 2 |
| L5 Retry while the original is still in flight | Mock `LatencyMs` 1,500; the driver resends the key 200 ms after the first send (C55) | Two ack lines for one key, overlapping in time | The retry renders current state ([D31](../../decisions.md#d31)), never a second attempt; one effect |

Classification rules live in CK11 (rule 4). A key that fits no case fails CK11. The report gives the count per case, so a run where L2 never happened can't claim to have tested it: **a case with zero classified payments is `INCONCLUSIVE` for that scenario.**

### 5.4 Database only

The existing `db/spike` pgbench matrix (`run-native.sh`) is the database-only benchmark. The suite runs `baseline` once per full suite and reports it next to the API result as "database ceiling", never as payment throughput.

## 6. Throughput and correctness definitions

**Completed payments/s** = distinct payments whose attempt reached a final committed outcome (captured by sale, authorized, or declined) with that final commit inside the measured window, divided by the window length. Captures, replays and `UNKNOWN` don't count ([glossary](../../glossary.md), D47).

- Source of truth: a database count over the window. `op_payments_completed_total` is the cross-check, and the two must agree within 0.5%; otherwise the evidence gate fails.
- Known imprecision: `now()` is transaction start, not commit time, so window edges shift by one transaction's duration (milliseconds). The report states it.

**Platform overhead** is per request: `T_overhead = T_request − T_psp`, where both come from the API's own clock around the same request. `T_request` runs from the first byte the endpoint handler sees to the response being written; `T_psp` is the measured time inside the PSP adapter call (`op_psp_wait_seconds`), not the configured mock latency. That is what `op_platform_overhead_seconds` records, and it is the gated value. The k6 figure "end-to-end minus configured latency" is a cross-check only, valid only when mock jitter is 0; with jitter it is reported and not compared.

**Correctness gates** use the existing checks plus CK11:

| Proposal's requirement | Check |
|---|---|
| Every successful payment has its ledger records | CK9, CK10 |
| No duplicate payment for a repeated key | CK2, CK11, C5 |
| No partial transaction commit | CK1 (balanced per transaction), CK9 (both ways) |
| No unexplained missing record | CK9, CK11 |
| No mutation of ledger entries | CK1 manifest |
| Reconciliation matches expected outcomes | CK10, CK11 |
| Acknowledged commits survive a restart | CK11 after C54 |
| Ambiguous timeouts reconciled without duplicates | CK2, CK11 after C52 and C43 |

CK4-CK8 keep their current applicability rules. Not applicable is shown as `n/a`, never as a pass.

## 7. Gates and verdict

`benchmarks/config/thresholds.json` holds every number, committed before a suite runs. A suite run records the file's hash.

| Gate | Requirement | Source |
|---|---|---|
| Correctness | All applicable CK1-CK11 pass in every scenario | `op-checks` |
| Duplicates, missing, partial | 0 | CK1, CK2, CK9, CK11 |
| Throughput | ≥ target completed payments/s in each of 3 target runs | DB count |
| Platform overhead | p50 ≤ 25 ms, p99 ≤ 100 ms over the window | `op_platform_overhead_seconds` |
| Error rate | < 0.1%, excluding 402, 409, 429, 503 | k6 + API |
| Bounded retries | No transaction with > 2 retries; app retries agree with `pg_stat_database.deadlocks` | Metrics, PG |
| Forced deadlock | TX1 and C34 pass, with deadlocks > 0 observed | Tests |
| Retry exhaustion | TX2 passes | Tests |
| Restart durability | C54 passes, with the fault verified | CK11 |
| Recovery | From fault removal: first success ≤ 30 s, platform p99 back under SLO ≤ 2 min; times from injection reported beside them (§5.2) | Ack log |
| PSP lifecycle | Every interrupted payment classified L0-L5, and each required case seen at least once (§5.3) | CK11 |
| Diagnostics | `pg_stat_statements` and the reproducibility manifest complete | Preflight, §8.1 |
| Soak | No upward trend in RSS, Gen 2 GC/s, pool wait (Ck6Trend slope test) | Samplers |
| Run validity | No dropped arrivals, generator CPU ≤ 80%, mock added p99 ≤ 5 ms, mix shares within 2 pp | `report.py` |
| Evidence | Every required file present, every metric non-null | `op-bench verify` |

**Each gate has its own result:** `PASS`, `FAIL`, `INCONCLUSIVE` or `NOT_APPLICABLE`. `NOT_APPLICABLE` is never a pass: a required gate that is not applicable makes the overall result `INCONCLUSIVE`.

**Overall verdict, evaluated in this order:**

1. Any correctness gate fails → **FAIL**.
2. Any run invalid, any required scenario skipped, any required value `null`, any fault not verified as injected, any required gate `INCONCLUSIVE` or `NOT_APPLICABLE` → **INCONCLUSIVE**.
3. Any performance or reliability gate missed → **FAIL**.
4. Otherwise → **PASS**.

**The environment label is separate from the verdict.** `PREVIEW` on the laptop. `RECORDED` only on Linux from Phase 3 on, with all checks applicable, which is the repository's existing word for the owner's "production acceptance" evidence. A laptop run can never be `RECORDED`.

| Verdict | Label | Meaning |
|---|---|---|
| PASS | PREVIEW | Every required gate passed on the laptop |
| FAIL | PREVIEW | A required gate failed on the laptop |
| INCONCLUSIVE | PREVIEW | A required capability or piece of evidence is missing |
| PASS | RECORDED | Every acceptance gate passed under the recorded-run rules |
| FAIL | RECORDED | An acceptance gate failed under the recorded-run rules |

Until Stage 2, the full suite's expected output looks like this, and that is the correct result:

```
Benchmark: OP-BENCH-20261110T0900Z-<sha>
Throughput:          PASS
Platform overhead:   PASS
Deadlock recovery:   PASS   (TX1, 37 deadlocks seen)
Idempotency:         PASS   (C5, C52, C55)
CK1-CK11:            PASS   (CK7, CK8 n/a)
Crash recovery:      NOT_APPLICABLE  (resolver arrives in Stage 2)
Overall:             INCONCLUSIVE (PREVIEW)
```

The numbers shown are placeholders, not results.

## 8. Report contract

`benchmarks/reports/<suite-id>/suite.json`, with `latest.json` and `latest.md` copied beside it. Every measured value carries its source.

```json
{
  "schema_version": "1.0",
  "suite_id": "OP-BENCH-20261109T1200Z-3faaaf6",
  "commit": "3faaaf6",
  "label": "PREVIEW",
  "result": "INCONCLUSIVE",
  "reasons": ["C4 reconciliation NOT_APPLICABLE until Stage 2"],
  "environment": {
    "host": "MacBook M5 Pro, 48 GB", "os": "macOS 27.0",
    "postgres": "18.x native, fsync_writethrough, quorum 2 standbys",
    "settings_checksum": "sha256:…", "thresholds_checksum": "sha256:…"
  },
  "target": { "completed_payments_per_second": 500, "window_seconds": 600, "repeats": 3 },
  "performance": {
    "completed_payments_per_second": {
      "value": null, "unit": "1/s", "source": "db_count",
      "cross_check": "op_payments_completed_total", "window": ["…", "…"], "runs": []
    },
    "platform_overhead_ms": { "p50": null, "p95": null, "p99": null, "source": "op_platform_overhead_seconds" },
    "end_to_end_ms": { "p50": null, "p95": null, "p99": null, "source": "k6 http_req_duration" },
    "error_rate": { "value": null, "source": "k6+api" }
  },
  "transactions": {
    "deadlocks": { "value": null, "source": "pg_stat_database.deadlocks" },
    "retries_by_sqlstate": null, "retry_exhausted": null, "lock_timeouts": null,
    "serialization_failures": null,
    "tx_duration_ms": { "p50": null, "p95": null, "p99": null },
    "pool_acquire_ms": { "p50": null, "p95": null, "p99": null },
    "lock_wait_ms": { "p50": null, "p95": null, "p99": null, "source": "pg_stat_activity sampled 100ms" }
  },
  "correctness": { "checks": { "CK1": "pass", "CK7": "n/a" }, "duplicates": null, "missing": null, "partial": null },
  "scenarios": [
    { "id": "C54", "verdict": "PASS", "fault_injected_at": "…", "fault_verified": true,
      "first_failure_at": "…", "first_success_after_clear_at": "…", "recovery_ms": null,
      "affected_requests": null, "evidence": ["runs/…/acks.ndjson", "runs/…/checks.json"] }
  ],
  "evidence": []
}
```

`null` means not measured, never zero. `suite.md` renders the same content: the headline numbers, the gate table, the scenario table, and a "what this does not establish" section, in the style of `BASELINE.md`.

### 8.1 Reproducibility manifest

Every suite and every run in it writes `manifest.json`. A result whose manifest is incomplete is `INCONCLUSIVE`, because it can't be compared with history.

| Field | Contents |
|---|---|
| `suite_id`, `run_ids` | The suite's ID and each run directory it used |
| `git` | Commit SHA, and proof the tree was clean (`git status --porcelain` empty) |
| `database` | PostgreSQL version; full `pg_settings` dump and its SHA-256; standby names and sync state; migration list with checksums; row counts per table at start |
| `workload` | `steps.json` entry, `mix.js` SHA-256, `TWO_CALL_SHARE`, k6 version, seed, refund-target count |
| `mock` | Full `/admin/config` body, mock commit and measured added p99 |
| `api` | Build configuration, pool size, commit wait limit, every limit from D34 |
| `environment` | Host model, cores, RAM, macOS version, power source, other heavy processes running, Docker or native, Toxiproxy version |
| `thresholds` | `thresholds.json` and its SHA-256 |

`op-bench verify` rebuilds the verdict from the stored evidence and manifest alone, without the live stack.

## 9. One-command interface

A root `Makefile` with thin targets over `op-bench`, so nothing is implemented twice:

```bash
make benchmark-preflight     # op-bench preflight
make benchmark-correctness   # dotnet test (unit, integration, acceptance) + op-checks smoke
make benchmark-reliability   # Reliability.Tests + fault scenarios
make benchmark-db            # db/spike baseline
make benchmark-e2e           # k6 steps, target ×3, stress, spike
make benchmark-soak          # 60 min, run separately because of its length
make benchmark               # preflight, correctness, reliability, e2e, report; exits 0 only on PASS
make benchmark-verify        # op-bench verify on the latest suite
```

`make benchmark` stops at the first infrastructure failure, keeps every partial artifact, and exits non-zero on FAIL or INCONCLUSIVE. Expected length without the soak: about 2.5 hours, mostly the three 10-minute target runs, their resets and their checks.

## 10. Build order

The owner's order, which builds evidence before anything that depends on it. Each item is test first, per the [build gate](../../delivery-workflow.md#build-gate). Exact files, signatures and tests are in the [build plan](../plans/2026-11-09-benchmark-suite.md).

| Priority | Delivers | Depends on |
|---|---|---|
| P0 | Reuse map and enabling settings: `pg_stat_statements`, `log_lock_waits`, `check-db-settings.sh`; Toxiproxy pinned | none |
| P1 | `op-bench` orchestrator: preflight, manifest, samplers, suite runner over `run.sh` | P0 |
| P2 | TxRunner and pool metrics (§4.4) and the lock-wait sampler (§4.5) | P0 |
| P3 | CK11 with corruption and zero-row tests (§4.3) | P0 |
| P4 | `Reliability.Tests`: TxRunner lab (TX1, TX2, serialization, lock timeout, pool) and product races (C34, C5, C6, C36, invalid input) | P2 |
| P5 | `FaultDriver` with ack log and `--lose-response`; PSP lifecycle scenarios C52, C55, C3 and the L0-L5 classification | P3 |
| P6 | Toxiproxy network faults, C43 network variant, C53, C54 restart, C4 crash, C2 | P5 |
| P7 | Gate evaluator, verdict, `suite.json`/`suite.md`, `op-bench verify`, Makefile | P1-P6 |
| P8 | First full suite run at the 500 completed payments/s milestone, recorded as `PREVIEW` | P7 |

## 11. Owner decisions

All six were approved on 2026-11-09, with the refinements now folded into §4.3, §4.5, §4.6, §5.2, §5.3, §6, §7 and §8.1. They are recorded as [D51](../../decisions.md#d51) and [D52](../../decisions.md#d52).

| # | Decision | Result |
|---|---|---|
| O1 | 500 completed payments/s is the laptop milestone, at step 6 (~1,000 req/s). The project target stays 5,000 | Approved |
| O2 | A laptop suite is `PREVIEW` only | Approved |
| O3 | Latency is gated on measured platform overhead, p50 ≤ 25 ms, p99 ≤ 100 ms | Approved, with per-request PSP time, not a fixed subtraction |
| O4 | New IDs CK11, TX1, TX2, C52, C53, C54, plus C55 for the in-flight retry case | Approved; CK11 widened |
| O5 | Recovery: first success ≤ 30 s, SLO ≤ 2 min | Approved, measured from both injection and removal |
| O6 | The D47 mix and the k6 harness stay unchanged | Approved |

**Non-negotiable constraints from the approval:** D28-D50 and D38 preserved; append-only ledger preserved; k6 workload and historical comparability preserved; existing C#/.NET 10 code and TxRunner reused; no architecture rewrites; no fabricated results; no PASS without independent correctness evidence; crash recovery stays `INCONCLUSIVE` until Stage 2; every result reproducible from its manifest.

## 12. Risks

- **One machine runs everything.** k6, the API, the mock, three PostgreSQL instances and the samplers share 18 cores and one SSD. At step 6, SSD flush may limit before the code does (bottleneck 9). The suite reports that as the first failing resource, not as the application's capacity.
- **The lock sampler misses short waits.** At 100 ms it can't see waits under 100 ms. It reports what it saw and says so.
- **C4 and C34 stay partial until Stage 2.** The full suite is `INCONCLUSIVE` until the reconciler exists. That is the honest result, not a defect in the suite.
- **The fault driver adds load.** It is capped at step 4 rates and its CPU is sampled. If it saturates, the scenario is invalid.

## 13. What this design does not change

D38, the append-only ledger, the D47 mix, `TxRunner`'s retry policy (2 retries, 5 ms × 2ⁿ full jitter, 200 ms cap), the D28 commit rules, CK1-CK10, and the production schema. The `txlab` schema exists only inside the reliability test fixture.
