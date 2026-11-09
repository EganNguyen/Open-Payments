# OP-BENCH Build Plan

> **For whoever executes this:** Follow [Delivery workflow](../../delivery-workflow.md#build-gate). This is build-gate work (DeepSeek), reviewed afterwards by Opus in a fresh session. Write each test first and watch it fail for the expected reason. Stop and escalate in `docs/gates/stage-1-escalations.md` on any conflict with the design; never decide it yourself. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Build the [OP-BENCH suite](../specs/2026-11-09-benchmark-suite-design.md) around the existing harness, so that one command produces a reproducible verdict on the 500 completed payments/s laptop milestone with independent correctness evidence.

**Approval:** [OP-BENCH design gate](../../gates/op-bench-design.md), [D51](../../decisions.md#d51), [D52](../../decisions.md#d52). This work is part of Stage 1 Task 17 (gate item W1). Log each task in `docs/gates/stage-1-progress.md` as `17.Pn`.

**Tech stack:** .NET 10, Npgsql, xUnit, Testcontainers (already used), k6 (unchanged), Toxiproxy, PostgreSQL 18 native and Docker.

## Global constraints

- **Do not edit:** `benchmarks/k6/mix.js`, `benchmarks/k6/lib/`, `benchmarks/profiles/steps.json`, `db/migrations/`, `tools/OpenPayments.Checks/sql/ck1.sql`-`ck10.sql`, `TxRunner`'s retry policy or commit handling, `docs/decisions.md`, the spec.
- `tools/OpenPayments.Bench` must not reference `OpenPayments.Transitions` or `OpenPayments.Api` ([D23](../../decisions.md#d23)).
- Metric labels: only `reason` and `sqlstate`, from the fixed lists in the spec §4.4. No IDs or keys as labels.
- `null` means not measured. No code path may turn a missing value into 0 or a pass.
- No result may be written by hand. Every number in a report comes from a file the suite wrote during the run.
- Commit and push to `main` per task, author `EganNguyen <tuannh2y@gmail.com>`, explicit paths only.
- After each task: `dotnet build -warnaserror`, `dotnet test`, and `.venv/bin/python -m pytest benchmarks/test_report.py`.

## Review focus

The reviewer will check, in this order: no gate can pass on missing evidence; CK11 fails on each corruption listed; the lab really raises deadlocks (server counter, not app counter); the fault scenarios verify the fault happened; the manifest is complete; nothing on the do-not-edit list changed.

---

## P0 — Enabling settings and tools

**Files:** `deploy/native/native.conf`, `deploy/compose/postgres/primary.macbook.conf`, `deploy/compose/postgres/primary.conf`, `scripts/native-stack.sh`, `scripts/check-db-settings.sh`, `deploy/compose/compose.yaml`, `benchmarks/README.md`

- [ ] In all three configs: `shared_preload_libraries = 'pg_stat_statements'`, `pg_stat_statements.track = top`, `pg_stat_statements.max = 10000`. In `native.conf` and `primary.macbook.conf` also `log_lock_waits = on` (`primary.conf` already has it).
- [ ] `native-stack.sh` and the compose init: `create extension if not exists pg_stat_statements` in `openpayments` after migrations. Not a migration file.
- [ ] `check-db-settings.sh`: add checks for `shared_preload_libraries` containing `pg_stat_statements`, the extension existing, `select count(*) from pg_stat_statements` succeeding as the benchmark role, and `log_lock_waits = on`.
- [ ] Add a `toxiproxy` service to `compose.yaml`, image pinned to the latest release tag at build time (tag, not `latest`), listening on `8474` with proxies created by `op-bench`, not by config. Document `brew install toxiproxy` for the native stack and record both versions in the README.
- [ ] **Test:** run `check-db-settings.sh` against a stack *before* the config change and watch the new checks fail; after, watch them pass. Paste both outputs into the progress line.

## P1 — `op-bench` orchestrator

**New projects:** `tools/OpenPayments.Bench/OpenPayments.Bench.csproj` (console, assembly name `op-bench`), `tests/OpenPayments.Bench.Tests/OpenPayments.Bench.Tests.csproj`. Add both to `OpenPayments.slnx`.

**Types (exact signatures):**

```csharp
namespace OpenPayments.Bench.Gates;

public enum GateStatus { Pass, Fail, Inconclusive, NotApplicable }
public enum Verdict { Pass, Fail, Inconclusive }
public enum EnvironmentLabel { Preview, Recorded }
public enum GateKind { Correctness, Validity, Evidence, Performance, Reliability }

public sealed record GateResult(
    string Id, GateKind Kind, GateStatus Status, string? Reason, IReadOnlyList<string> Evidence);

public static class VerdictEvaluator
{
    // Spec §7 order: correctness FAIL > any required Inconclusive/NotApplicable/missing > other FAIL > PASS.
    public static Verdict Evaluate(IReadOnlyList<GateResult> gates, IReadOnlySet<string> requiredGateIds);
}

public static class LabelEvaluator
{
    // Recorded only when os == Linux, phase >= 3, all checks applicable, clean tree. Otherwise Preview.
    public static EnvironmentLabel Evaluate(RunManifest manifest, int phase, bool allChecksApplicable);
}
```

```csharp
namespace OpenPayments.Bench.Evidence;

public sealed record RunManifest(/* fields exactly as spec §8.1 */)
{
    public IReadOnlyList<string> MissingFields();   // empty when complete
}

public sealed class PgSnapshot
{
    public static Task CaptureAsync(string connectionString, string outputPath, CancellationToken ct);
}

public sealed class ResourceSampler : IAsyncDisposable
{
    public static ResourceSampler Start(IReadOnlyDictionary<string, int> pidsByRole, TimeSpan interval, string outputPath);
}
```

```csharp
namespace OpenPayments.Bench.Scenarios;

public interface IScenario
{
    string Id { get; }                       // "STEP6", "C52", "TX1", ...
    Task<ScenarioResult> RunAsync(ScenarioContext context, CancellationToken ct);
}

public sealed record ScenarioResult(
    string Id, IReadOnlyList<GateResult> Gates, IReadOnlyDictionary<string, object?> Values,
    IReadOnlyList<string> EvidencePaths);
```

- [ ] `op-bench preflight`, `run <suite>`, `verify <suite-id>`, `report <suite-id>` per spec §4.1. Suites: `smoke`, `correctness`, `reliability`, `performance`, `full`.
- [ ] `StepScenario` runs `benchmarks/run.sh <step>` and reads `config.json`, `k6-summary.json`, `checks.json`, `result.md` validity, `resources.json`, `shares.json` as evidence. It does not change `run.sh` behaviour.
- [ ] `TargetScenario` runs step 6 three times, each after the [full reset](../../../benchmarks/README.md#full-benchmark-reset), and counts completed payments from the database over the measured window (spec §6), cross-checked against `op_payments_completed_total` within 0.5%.
- [ ] `benchmarks/config/thresholds.json` with every value from spec §7; its SHA-256 goes into the manifest.
- [ ] **Tests (`Bench.Tests`, pure, no database):**
  - `Correctness_fail_beats_inconclusive`
  - `Required_not_applicable_gate_makes_suite_inconclusive`
  - `Missing_required_gate_makes_suite_inconclusive`
  - `Performance_fail_without_correctness_fail_is_fail`
  - `All_required_pass_is_pass`
  - `Laptop_manifest_is_never_recorded`
  - `Manifest_missing_any_field_is_reported`
  - `Null_value_never_becomes_pass` (feed a gate builder a null p99, expect `Inconclusive`)

## P2 — TxRunner metrics and lock-wait sampling

**Files:** `src/OpenPayments.Transitions/TxRunner.cs`, `src/OpenPayments.Transitions/Histogram.cs` (new), `src/OpenPayments.Api/Metrics.cs`, `docs/metrics.md`, `tests/OpenPayments.Integration.Tests/TxRunnerTests.cs`, `tests/OpenPayments.Acceptance.Tests/MetricsTests.cs`, `tools/OpenPayments.Bench/Evidence/LockSampler.cs`

- [ ] Move the private `Histogram` from `Metrics.cs` to `OpenPayments.Transitions/Histogram.cs` as `public sealed class Histogram(double[] bounds)`, behaviour unchanged. `Metrics.cs` uses it from there.
- [ ] `TransitionMetrics` gains: `Attempt()`, `Failed(string reason)` with reasons `retry_exhausted`, `lock_timeout`, `pool_wait`, `commit_unknown`, `other`; `TxDuration(TimeSpan)`; `PoolAcquire(TimeSpan)`; `RetriesPerTransaction(int)`. Call sites only where TxRunner already decides the outcome; no new control flow, no change to which errors retry.
  - `op_tx_duration_seconds` buckets: `0.001, 0.0025, 0.005, 0.01, 0.025, 0.05, 0.1, 0.25, 0.5, 1, 3`
  - `op_db_pool_acquire_seconds` buckets: `0.0005, 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 2`
  - `op_tx_retries_per_transaction` buckets: `0, 1, 2`
- [ ] `Metrics.cs` renders `op_tx_attempts_total`, `op_tx_failed_total{reason}`, `op_tx_duration_seconds`, `op_db_pool_acquire_seconds`, `op_tx_retries_per_transaction`. Add each row to `docs/metrics.md`.
- [ ] `LockSampler`: every 100 ms, `select pid, now() - state_change as waited, query from pg_stat_activity where wait_event_type = 'Lock'`, plus `pg_blocking_pids`. Output: sampled lock-wait seconds, p50/p95/p99 of observed wait ages, top 10 blocker/blocked query pairs. The report labels it `sampled 100ms`.
- [ ] **Tests:**
  - `TxRunnerTests.Counts_attempts_and_retry_exhaustion` (existing 40P01 method: 3 attempts, `retry_exhausted` +1)
  - `TxRunnerTests.Counts_lock_timeout_as_failed_not_retried`
  - `TxRunnerTests.Records_duration_and_pool_acquire_for_committed_transaction`
  - `MetricsTests.Renders_new_tx_series_with_bounded_labels`
  - `Bench.Tests.LockSampler_sees_a_held_lock` (Testcontainers: one session holds a row lock 1 s, another waits; sampler reports ≥ 1 wait sample)

## P3 — CK11

**Files:** `tools/OpenPayments.Checks/Ck11ClientView.cs` (new), `tools/OpenPayments.Checks/sql/ck11.sql` (new), `tools/OpenPayments.Checks/Program.cs`, `tools/OpenPayments.Checks/CheckRunner.cs`, `tests/OpenPayments.Checks.Tests/Ck11Tests.cs` (new), `docs/verification.md` is already updated.

**Ack log format** (one JSON object per line, written by P5's driver; P3 defines and owns the parser):

```json
{"seq":1,"key":"…","merchant_id":"…","route":"POST /v1/payments","sent_at":"…","first_byte_at":"…",
 "done_at":"…","status":201,"body_read":true,"payment_id":"…","state":"CAPTURED","error":null,"replay_of":null}
```

- [ ] `op-checks --acks <path>` loads the file into a temporary table `ck_acks` with `COPY`, then runs `ck11.sql`. Without `--acks`, CK11 is reported not applicable, never passed.
- [ ] `ck11.sql` returns one row per violation, with `sample_id` and `rule`, for the six rules in spec §4.3. Join keys to payments through `idempotency_keys (merchant_id, key)`; the effect log through `ck.psp_effects`, as CK2 does.
- [ ] Classification output: a separate result set, one row per unanswered or interrupted key, with `case` in `L0`-`L5` per spec §5.3. A key matching no case is a violation. Write counts per case into `checks.json` under `ck11.cases`.
- [ ] **Tests (`Ck11Tests`, Testcontainers, use the existing `Fixture.cs` pattern):**
  - `Passes_on_consistent_acks_and_rows`
  - `Fails_when_acknowledged_payment_is_deleted`
  - `Fails_when_persisted_state_contradicts_answer` (ack `CAPTURED`, row `FAILED`)
  - `Allows_failed_row_for_declined_answer`
  - `Allows_unknown_row_for_503_after_transaction_a`
  - `Fails_when_replay_got_a_different_payment_id`
  - `Fails_when_unanswered_key_has_rows_but_fits_no_case`
  - `Classifies_each_case_L0_to_L5` (one hand-built fixture per case)
  - `Fails_on_zero_rows` (empty ack log and empty tables)

## P4 — Reliability tests

**New project:** `tests/OpenPayments.Reliability.Tests/` referencing `OpenPayments.Transitions` and `OpenPayments.Domain`. All tests carry `[Trait("Category", "Reliability")]`. Each test writes `bin/reliability/<test>.json` with its counts, which `op-bench` collects.

**Files:** `TxLabFixture.cs`, `TxLabTests.cs`, and `tests/OpenPayments.Acceptance.Tests/C34RaceTests.cs`, `InvalidInputTests.cs`.

- [ ] `TxLabFixture` starts PostgreSQL with Testcontainers, creates `schema txlab` with `accounts (id int primary key, value bigint not null)` and two rows, and drops the schema on dispose. Nothing in `db/migrations`.
- [ ] Tests, exactly as spec §4.6:
  - `TX1_opposing_transfers_deadlock_and_recover`: 100 pairs, released by one `Barrier`; assert `pg_stat_database.deadlocks` delta > 0, 200 commits, final values equal the arithmetic sum, `op_tx_retries_per_transaction` ≤ 2 for all.
  - `TX2_retry_exhaustion_fails_explicitly`
  - `Serialization_failure_is_retried_and_counted_as_40001`
  - `Lock_timeout_is_not_retried`
  - `C2_pool_exhaustion_is_bounded` (`Maximum Pool Size=4`, 50 holders, `Timeout=1`; failures are `BusyException("pool_wait")`; after release a new transaction commits on the same runner)
- [ ] `C34RaceTests`: capture and 10 refunds on one payment, released together through the API test host; assert at most 2 retries per transaction, one PSP effect per operation, CK1, CK2, CK3 pass; record the observed 40P01 count (zero allowed). Reconciler part: assert nothing and record `not_applicable`.
- [ ] Tag existing `C5_*`, `C6_*`, `C36_*` tests with the `Reliability` trait. Do not change their bodies.
- [ ] `InvalidInputTests`: bad currency, non-positive amount, missing key, key reused on another route (422); each asserts a 4xx and that no row was added to `payments`, `payment_attempts`, `ledger_transactions`, `ledger_entries`, `outbox`.

## P5 — Fault driver and PSP lifecycle scenarios

**Files:** `tools/OpenPayments.Bench/FaultDriver/*.cs`, `tools/OpenPayments.Bench/Scenarios/C52.cs`, `C55.cs`, `C3.cs`, `tests/OpenPayments.Bench.Tests/FaultDriverTests.cs`, `tests/OpenPayments.MockPsp.Tests/MockPspTests.cs`

```csharp
public sealed record DriverOptions(
    Uri ApiBase, string Token, double RatePerSecond, TimeSpan Duration, string AckLogPath,
    bool LoseResponse, TimeSpan? ResendAfter, string KeyPrefix);

public sealed class FaultDriver
{
    public Task<DriverSummary> RunAsync(DriverOptions options, CancellationToken ct);
}

public sealed record DriverSummary(long Scheduled, long Sent, long LateArrivals, long Answered, long Unanswered);
```

- [ ] Open-model scheduling on a `PeriodicTimer`; a send that starts more than 10 ms late counts as a late arrival, and any late arrival makes the scenario invalid.
- [ ] Keys are `"{KeyPrefix}-{seq}"`, deterministic. One ack line per request, written after the response or the error, flushed every 100 lines and on exit.
- [ ] `LoseResponse`: read the status line and headers with `HttpCompletionOption.ResponseHeadersRead`, then abort; write `body_read: false`; resend the same key once, recording `replay_of`.
- [ ] Cap the rate at 250 req/s in code.
- [ ] **First test, before anything else in P5:** `MockPspTests.ApproveThenHang_writes_effect_before_hanging` — proves L2 can be produced (gate risk). If it fails, stop and escalate.
- [ ] Scenarios per spec §5.2-5.3: C52 (lose 200 responses at 50 req/s), C55 (`LatencyMs` 1,500, resend after 200 ms), C3 (`ApproveThenHangRate` 0.1 without a kill). Each runs `op-checks` with `--acks`, requires CK2 and CK11, and returns `Inconclusive` for its lifecycle case if that case's count is zero.
- [ ] **Tests:** `FaultDriverTests.Writes_one_ack_line_per_request_including_resets`, `Resend_reuses_the_key`, `Lose_response_records_body_not_read`, `Late_arrival_is_counted`.

## P6 — Network, restart and crash faults

**Files:** `tools/OpenPayments.Bench/Faults/ToxiproxyClient.cs`, `PgControl.cs`, `ProcessKiller.cs`, `Scenarios/C43Net.cs`, `C53.cs`, `C54.cs`, `C4.cs`, `tests/OpenPayments.Bench.Tests/FaultVerificationTests.cs`

- [ ] `ToxiproxyClient` over the HTTP API: create proxy, add and remove `latency`, `reset_peer`, `timeout`, `limit_data` toxics. The API's connection string points at the proxy only for fault scenarios.
- [ ] Every fault scenario records `fault_injected_at`, `fault_removed_at`, and **proof the fault happened**: C53 at least one request slower than the added latency; C54 the server's start time changed (`pg_postmaster_start_time()`) and at least one connection error in the ack log; C4 the API PID changed. No proof → `Inconclusive`.
- [ ] Recovery values exactly as spec §5.2, all four, from ack-log timestamps.
- [ ] C4: run at step 4 rate with mock `LatencyMs` 2,000 for L1 and, separately, `ApproveThenHangRate` 1.0 for L2; `kill -9` the API 10 s in; restart it; run `op-checks --acks`. Resolution gate is always `NotApplicable` until Stage 2.
- [ ] C54: `pg_ctl restart -m fast` on the native primary at step 4; then a second run with `-m immediate`.
- [ ] **Tests:** `Scenario_without_fault_proof_is_inconclusive`, `Recovery_times_are_computed_from_both_injection_and_removal`.

## P7 — Report, verify and the one command

**Files:** `tools/OpenPayments.Bench/Report/SuiteReport.cs`, `MarkdownWriter.cs`, `Makefile` (new, repository root), `benchmarks/README.md`, `.gitignore`

- [ ] `suite.json` exactly as spec §8, plus `manifest.json`. `latest.json` and `latest.md` are copies. `benchmarks/reports/` is git-ignored except a `README.md`.
- [ ] `op-bench verify <suite-id>` rebuilds the verdict from stored files only and exits non-zero if it differs from the stored one.
- [ ] `Makefile` targets exactly as spec §9, each one line calling `dotnet tools/OpenPayments.Bench/bin/.../op-bench.dll`. `make benchmark` exits 0 only on `PASS`.
- [ ] `suite.md` ends with "What this does not establish", listing at least: not a recorded result on a laptop; no Linux `C` or `N`; resolution `NOT_APPLICABLE` until Stage 2.
- [ ] **Tests:** `Report_round_trips_through_verify`, `Tampered_value_fails_verify`, `Markdown_shows_not_applicable_as_not_applicable`.

## P8 — First full suite run

- [ ] From a clean tree and a full reset: `make benchmark-preflight`, then `make benchmark`, then `make benchmark-soak` separately.
- [ ] Copy `suite.md` into a tracked record `benchmarks/OP-BENCH-<date>.md` with the suite ID and commit. Do not edit any number.
- [ ] The expected overall result is `INCONCLUSIVE (PREVIEW)` because of C4's resolution gate. Any other result, or any FAIL, is reported as found. A throughput miss is a result, not a defect to fix in this plan: record the first failing resource and stop.
- [ ] Progress line: suite ID, commit, verdict, and the per-gate table.
