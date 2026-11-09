# OP-BENCH: End-to-End Benchmark and Reliability Suite

A review of the proposed OP-BENCH v1 specification against this repository, and the design that replaces it. The suite is C# (.NET 10) apart from k6, which stays as the throughput generator.

Status: `Proposed`, design gate, awaiting owner approval · Last updated: 2026-11-09 · Starts from commit `3faaaf6`

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
| R3 | High | **"500 committed payments/s" is the wrong unit, and the wrong number.** The glossary defines *completed payments/s*, which includes declines and excludes captures, retries and `UNKNOWN`. The project target is 5,000. At the D47 mix half of all requests start a payment, so 500 payments/s is ~1,000 req/s, which is **step 6**, not step 5. | Use "completed payments/s" as the glossary defines it. Treat 500 as a *laptop milestone* the owner sets (NEEDS-OWNER O1), mapped to step 6. |
| R4 | High | **The 80/10/5/5 workload replaces the committed D47 mix** (40 sale, 10 authorize, 10 capture, 15 status, 10 refund, 10 retry, 5 other). Changing it invalidates every earlier run and the capacity model, which depends on `C` and `T` coming from one mix. | Throughput scenarios use D47 unchanged. Concurrent duplicates get their own scenario (C5), not a share of the mix. |
| R5 | High | **A MacBook result can't be PASS under the existing trust rules.** Only a Linux run from Phase 3 on, with all checks applicable, is a *Recorded* result. | The suite reports a verdict *and* a label: `PREVIEW` on the laptop, `RECORDED` only where the existing rules allow. A laptop can give `PASS (PREVIEW)`, never `PASS (RECORDED)`. |
| R6 | High | **Crash recovery can't pass yet.** "Incomplete work is safely reconciled" needs the reconciler, which is Stage 2 work. C4 and C3 can't exercise real behaviour today. | Until Stage 2, crash scenarios check what is buildable now (no acknowledged answer lost, no second PSP effect, every interrupted attempt findable) and report the reconciliation part as `NOT_APPLICABLE`, which keeps the suite `INCONCLUSIVE` for that gate. |
| R7 | Medium | **A two-account transfer fixture proves the retry loop, not the product.** The product's real deadlock risk is several writers on one `payments` row (capture, refund, reconciler), which is C34. | Keep the fixture, in its own `txlab` schema, as the TxRunner lab. Add the real-path C34 race through the HTTP API as the product evidence. |
| R8 | Medium | **"Lock wait p50/p95/p99" has no source today.** `pg_locks_count` counts locks held, not waits, and PostgreSQL keeps no lock-wait histogram. | Sample `pg_stat_activity` for `wait_event_type = 'Lock'` every 100 ms, plus `log_lock_waits` lines. Report it as a sampled estimate, labelled so. |
| R9 | Medium | **Retry exhaustion can't be forced with real deadlocks.** TxRunner retries 40001/40P01 at most twice, and real cycles rarely recur three times in a row. | The lab raises a real `PostgresException` with SQLSTATE 40P01 from PL/pgSQL on every attempt (the method `TxRunnerTests` already uses). That proves the bound and the `retry_exhausted` 503 without relying on timing. |
| R10 | Medium | **"Survives a database restart" needs a client-side record of what was acknowledged.** Reconciliation alone can't tell a lost commit from one that never happened. | A C# fault driver keeps an append-only *ack log* (key, status, payment ID, outcome) and a new check proves every acknowledged answer exists in the database with the same outcome. |
| R11 | Low | Docker durability isn't equivalent to native (Docker flush ~60 µs, native ~3.26 ms). | Throughput scenarios run native only. Docker is for correctness and fault tests, where flush speed doesn't decide the result. |
| R12 | Low | `pg_stat_statements` isn't enabled in either PostgreSQL config, and `log_lock_waits` isn't set. | Enable both in `native.conf` and `primary.macbook.conf`. `scripts/check-db-settings.sh` verifies them in preflight. |
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
| `op-bench preflight` | Clean git tree; native stack up; `check-db-settings.sh` passes; quorum standbys healthy; Prometheus targets up; mock PSP capacity record present; disk headroom; records a config checksum |
| `op-bench run <suite>` | Runs a suite (`smoke`, `correctness`, `reliability`, `performance`, `full`) scenario by scenario, snapshots evidence before and after each, never resets statistics without exporting them first |
| `op-bench verify <suite-id>` | Re-runs evidence completeness and gate evaluation on a stored suite, so a report can be checked by someone else |
| `op-bench report <suite-id>` | Writes `suite.json` and `suite.md` |

Throughput scenarios shell out to `benchmarks/run.sh`, so the existing validity rules apply unchanged. The orchestrator reads each run directory as evidence; it doesn't rewrite them.

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

### 4.3 `op-checks` CK11 (new check): acknowledged answers survive

> **CK11:** every request the client saw answered with 2xx or 402 has its payment in the database, with the same payment ID and an outcome consistent with the answer. Every key appears at most once in `idempotency_keys`. Every replay got the same payment ID as the original.

It reads `acks.ndjson` (`--acks <path>`) and the database. Like every CK, it must fail on injected corruption (delete an acknowledged payment, change an outcome) and on zero rows. Adding a CK number needs owner approval (O4).

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
| `Lab_lock_timeout_is_not_retried` | A third session holds the row past `lock_timeout` (2 s) | One attempt; `BusyException("lock_timeout")`; 55P03 counted |
| `Lab_pool_exhaustion_is_bounded` | Pool of 4, 50 concurrent holders | Waits are bounded by the pool timeout; failures are `pool_wait`, never a hang; recovery without a restart (C2) |

**Product races, through the HTTP API.**

| Test | Existing ID | Pass |
|---|---|---|
| Capture and refunds race on one payment | C34 (reconciler part is `NOT_APPLICABLE` until Stage 2) | Any 40P01 retried at most twice; exactly one PSP effect per operation; CK1, CK2, CK3 pass |
| 100 concurrent requests with one key | C5 | One payment, one PSP charge, identical body to all 100 |
| 50 concurrent refunds over the captured amount | C6 | Refunded + pending ≤ captured |
| Two captures with different keys | C36 | One PSP capture; the other 409 or current status |
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

Each scenario is mapped to its existing ID where one exists, so results land in the records that already expect them. New IDs need approval (O4).

### 5.1 Performance (k6, native stack)

| Scenario | Steps / ID | Duration | Measures |
|---|---|---|---|
| Warm-up | not judged | 2 min | Excluded from every window |
| Baseline | Steps 0-1 | 5 min each | Latency floor, correctness at low rate |
| Load | Steps 2-5 | 5-10 min | Throughput and latency per step |
| **Target** | Step 6 (1,000 req/s ≈ 500 completed payments/s) | 10 min measured, ×3 from a reset | The acceptance number, if the owner sets 500 (O1) |
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
| API `kill -9` under load | C4, partial until Stage 2 | Step 4 | No acknowledged answer lost (CK11); no second PSP effect (CK2); every interrupted attempt is in `UNKNOWN` or earlier and findable. Reconciliation: `NOT_APPLICABLE` |
| Pool exhaustion | C2 | Burst | Bounded waits, controlled failures, recovery without restart |
| Invalid input | C31a subset | n/a | Rejected, no money-table mutation |

**Recovery time** is first successful committed request after the fault is cleared minus fault clear time, and separately time until platform p99 is back under the SLO. Both come from the ack log timestamps, not from the server.

### 5.3 Database only

The existing `db/spike` pgbench matrix (`run-native.sh`) is the database-only benchmark. The suite runs `baseline` once per full suite and reports it next to the API result as "database ceiling", never as payment throughput.

## 6. Throughput and correctness definitions

**Completed payments/s** = distinct payments whose attempt reached a final committed outcome (captured by sale, authorized, or declined) with that final commit inside the measured window, divided by the window length. Captures, replays and `UNKNOWN` don't count ([glossary](../../glossary.md), D47).

- Source of truth: a database count over the window. `op_payments_completed_total` is the cross-check, and the two must agree within 0.5%; otherwise the evidence gate fails.
- Known imprecision: `now()` is transaction start, not commit time, so window edges shift by one transaction's duration (milliseconds). The report states it.

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
| Recovery | Under the configured recovery objective (proposed: 30 s to first success, 2 min to SLO) | Ack log |
| Soak | No upward trend in RSS, Gen 2 GC/s, pool wait (Ck6Trend slope test) | Samplers |
| Run validity | No dropped arrivals, generator CPU ≤ 80%, mock added p99 ≤ 5 ms, mix shares within 2 pp | `report.py` |
| Evidence | Every required file present, every metric non-null | `op-bench verify` |

**Verdict rules, evaluated in this order:**

1. Any correctness gate fails → **FAIL**.
2. Any run invalid, any required scenario skipped, any required value `null`, any fault not verified as injected → **INCONCLUSIVE**.
3. Any performance or reliability gate missed → **FAIL**.
4. Otherwise → **PASS**.

The label is separate: **PREVIEW** on the laptop; **RECORDED** only on Linux from Phase 3 on, with all checks applicable. The suite prints both, for example `PASS (PREVIEW)`.

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

## 10. Build tasks

Ordered so each task's evidence exists before the next depends on it. Each task is test first, per the [build gate](../../delivery-workflow.md#build-gate).

| Task | Delivers | Depends on |
|---|---|---|
| B1 | `pg_stat_statements` and `log_lock_waits` in both configs; `check-db-settings.sh` verifies them | none |
| B2 | TxRunner and pool metrics (§4.4), with `MetricsTests` for every new series | none |
| B3 | `Reliability.Tests` TxRunner lab: TX1, TX2, serialization, lock timeout, pool exhaustion | B2 |
| B4 | CK11 in `op-checks`, with corruption and zero-row tests | none |
| B5 | `op-bench` skeleton: preflight, evidence samplers, report writer, `thresholds.json`, `verify` | none |
| B6 | `FaultDriver` with ack log and `--lose-response`; C52 | B4, B5 |
| B7 | Toxiproxy service; C43 network variant, C53, C54, C4 (partial), C2 | B6 |
| B8 | Product races C34, C5, C6, C36 and invalid input through the API | B3 |
| B9 | Performance suite wiring over `run.sh`: target ×3, stress, spike, soak; DB-count window; spike recovery | B5 |
| B10 | Makefile, `make benchmark`, README section, first full PREVIEW suite run recorded | all |

## 11. Decisions needed from the owner

| # | Decision | Recommendation |
|---|---|---|
| O1 | Is 500 completed payments/s the laptop milestone? | Yes, as a laptop milestone mapped to step 6. The project target stays 5,000, judged on Linux. |
| O2 | Accept that a laptop suite can only be `PREVIEW`? | Yes. It keeps the existing trust rules intact. |
| O3 | Platform-overhead SLO (p50 ≤ 25 ms, p99 ≤ 100 ms) as the latency gate, instead of the proposal's end-to-end 200 / 500 ms? | Yes. End-to-end includes the PSP and is set by the mock's latency setting. |
| O4 | Approve new IDs CK11, TX1, TX2, C52, C53, C54 in `verification.md` | Yes. |
| O5 | Recovery objective for C53, C54: 30 s to first success, 2 min to SLO? | Yes, as a starting value, replaced by measurement. |
| O6 | Throughput scenarios keep the D47 mix, and the proposal's 80/10/5/5 mix is dropped | Yes ([D47](../../decisions.md#d47)). |

## 12. Risks

- **One machine runs everything.** k6, the API, the mock, three PostgreSQL instances and the samplers share 18 cores and one SSD. At step 6, SSD flush may limit before the code does (bottleneck 9). The suite reports that as the first failing resource, not as the application's capacity.
- **The lock sampler misses short waits.** At 100 ms it can't see waits under 100 ms. It reports what it saw and says so.
- **C4 and C34 stay partial until Stage 2.** The full suite is `INCONCLUSIVE` until the reconciler exists. That is the honest result, not a defect in the suite.
- **The fault driver adds load.** It is capped at step 4 rates and its CPU is sampled. If it saturates, the scenario is invalid.

## 13. What this design does not change

D38, the append-only ledger, the D47 mix, `TxRunner`'s retry policy (2 retries, 5 ms × 2ⁿ full jitter, 200 ms cap), the D28 commit rules, CK1-CK10, and the production schema. The `txlab` schema exists only inside the reliability test fixture.
