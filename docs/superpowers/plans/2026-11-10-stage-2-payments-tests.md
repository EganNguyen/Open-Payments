# Stage 2 Payments/s Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Run Stage 2's 10-minute (500 req/s, three times) and 1-hour (250 req/s) payments/s tests on the mock PSP, with recovery live, and judge them by the Stage 1 gates plus the Stage 2 gates.

**Architecture:** The existing harness is extended, not replaced. `run.sh --stage phase2` applies a committed mock profile, prefills an expiry backlog, waits for a bounded drain, and calls a new evidence collector. A new `benchmarks/phase2.py` holds the Stage 2 gate logic as pure functions; `report.py` calls it and adds its table to `result.md`. A summary command takes the minimum of three repeats.

**Tech Stack:** bash, Python 3 (stdlib only, pytest), psql, k6 (unchanged `mix.js`), Prometheus HTTP API, the C# mock PSP.

**Spec:** [Stage 2 payments/s tests design](../specs/2026-11-10-stage-2-payments-tests-design.md). This plan is Step 3b of [Task 36](2026-11-10-stage-2-psp-and-recovery.md#task-36-benchmarks-steps-0-5-with-recovery-and-g2).

**Starts after:** Stage 2 Tasks 19-31 are done. These tests need the mock's callbacks and knobs (Task 24), the callback route (Task 25), the parked-event worker (Task 26), the reconciler (Task 27), the timers (Task 28) and key expiry (Task 30). If a name this plan consumes from those tasks differs from what they shipped, **stop and escalate**; don't rename either side.

## Global Constraints

- Step 200: 500 req/s, 600 s measured, 3 repeats each from a full benchmark reset; the result is the minimum of the three with the spread.
- Step 201: 250 req/s, 3,600 s measured, 1 run from a full reset.
- One API instance, one reconciler, one mock PSP. Mock PSP only; no Stripe, no PSP keys.
- Phase2 mock profile, exactly: `latency_ms` 200, `jitter_ms` 0, `decline_rate` 0.03, `error_rate` 0.02, `approve_then_hang_rate` 0.005, `late_landing_rate` 0.005, `callback_rate` 1.0, `callback_duplicate_rate` 0.05, `callback_reorder_window_ms` 500, `rate_limit_per_s` 1000.
- Expiry prefill: 2,000,000 resolved keys with `resolved_at` older than 24 h, spread over the 16 partitions.
- Drain limit 300 s. Parked limit 15 min. Recovery quota = `psp_accounts.recovery_share` × the account's limit.
- Stage 1 gates unchanged: platform p50 ≤ 25 ms, p99 ≤ 100 ms (k6 end-to-end minus the configured 200 ms, valid because jitter is 0); errors < 0.1% excluding 402, 409, 429, 503; no dropped arrivals; generator CPU ≤ 80%; mock adds ≤ 5 ms at p99; shares within 2 pp; DB and metric completed counts within 0.5%; CK1-CK10 pass, with CK7 and CK8 required in phase2.
- Gate results are `PASS`, `FAIL`, `INCONCLUSIVE` or `NOT_APPLICABLE`. A required gate that is `NOT_APPLICABLE` or a missing value makes the run `INCONCLUSIVE`. Every result is labelled preview.
- Verdict order: correctness fails → FAIL; invalid, missing or inconclusive → INCONCLUSIVE; performance or Stage 2 gate missed → FAIL; else PASS.
- No threshold, rate or profile value changes after a run has been seen. A change goes back to design review.
- No asynchronous replication, `synchronous_commit` below `on`, `fsync = off` or RAM disk, ever ([D57](../../decisions.md#d57)).
- Commits authored as EganNguyen, to main.

## Review Focus

1. **Reconciler not running:** a run with recovery dead would just fail the drain after 5 minutes and look like a slow system. `run.sh` must refuse to start when the reconciler isn't up. Test in Task 4.
2. **Prometheus unreachable or a series empty:** each Stage 2 value it feeds must be `null`, and its gate `INCONCLUSIVE`, never `PASS` on a default 0. Test in Task 2.
3. **An old mock that ignores the phase2 knobs:** the run would test nothing while every gate passes. `run.sh` must read back `/admin/config` and abort on any mismatch, and the "unknowns seen" gate catches zero faults. Tests in Tasks 2 and 4.
4. **Expiry prefill on a database that wasn't reset:** repeated seeding must not fail or double the backlog. The seed inserts with `on conflict do nothing`, and the gate compares the backlog measured at start with the one at end, never the configured 2,000,000. Test in Task 4.
5. **Repeat summary with fewer than three runs, or with an invalid one:** it must say `INCONCLUSIVE`, never take a minimum over invalid runs. Test in Task 3.

---

### Task 1: Step profiles and the phase2 profile

**Files:**
- Modify: `benchmarks/profiles/steps.json`
- Create: `benchmarks/profiles/phase2.json`, `benchmarks/phase2.py`, `benchmarks/test_phase2.py`

**Interfaces:**
- Produces: `steps.json` entries `{"step": 200, "rate": 500, "duration_seconds": 600, "purpose": "Stage 2 10-minute test: 500 req/s with recovery live, x3 from a reset"}` and `{"step": 201, "rate": 250, "duration_seconds": 3600, "purpose": "Stage 2 1-hour test: 250 req/s with recovery live"}`.
- Produces: `phase2.json` with keys `mock` (the Global Constraints knobs), `expiry_prefill_keys` (2000000), `thresholds` (`drain_limit_seconds` 300, `parked_limit_seconds` 900, `trend_min_duration_seconds` 3600, `repeats_step_200` 3).
- Produces: `phase2.load_profile(path: Path) -> dict`. Raises `ValueError` naming the missing key if any of `mock`, `expiry_prefill_keys` or `thresholds` is missing.

- [ ] **Step 1: Write the failing tests** in `benchmarks/test_phase2.py`

```python
def test_profile_has_the_spec_values():
    p = load_profile(Path("benchmarks/profiles/phase2.json"))
    assert p["mock"] == {"latency_ms": 200, "jitter_ms": 0, "decline_rate": 0.03,
        "error_rate": 0.02, "approve_then_hang_rate": 0.005, "late_landing_rate": 0.005,
        "callback_rate": 1.0, "callback_duplicate_rate": 0.05,
        "callback_reorder_window_ms": 500, "rate_limit_per_s": 1000}
    assert p["expiry_prefill_keys"] == 2_000_000
    assert p["thresholds"]["drain_limit_seconds"] == 300
    assert p["thresholds"]["parked_limit_seconds"] == 900

def test_profile_missing_key_names_it(tmp_path):
    f = tmp_path / "p.json"; f.write_text('{"mock": {}, "thresholds": {}}')
    with pytest.raises(ValueError, match="expiry_prefill_keys"):
        load_profile(f)

def test_steps_200_and_201_exist():
    steps = {s["step"]: s for s in json.load(open("benchmarks/profiles/steps.json"))}
    assert (steps[200]["rate"], steps[200]["duration_seconds"]) == (500, 600)
    assert (steps[201]["rate"], steps[201]["duration_seconds"]) == (250, 3600)
```

- [ ] **Step 2: Run to verify they fail:** `cd benchmarks && python3 -m pytest test_phase2.py -v`. Expected: FAIL, `ModuleNotFoundError: phase2`.
- [ ] **Step 3: Implement** the two JSON files and `load_profile` in `benchmarks/phase2.py`.
- [ ] **Step 4: Run to verify they pass:** same command. Expected: 3 passed.
- [ ] **Step 5: Commit** `bench: add the Stage 2 step and mock profiles`.

### Task 2: Stage 2 gates as pure functions

**Files:**
- Modify: `benchmarks/phase2.py`, `benchmarks/test_phase2.py`

**Interfaces:**
- Produces, in `phase2.py`:
  - `@dataclass GateResult: name: str; status: str; detail: str`. `status` is one of `PASS`, `FAIL`, `INCONCLUSIVE`, `NOT_APPLICABLE`.
  - `drain_gate(unresolved_after: Optional[int], drain_seconds: Optional[float], limit_s: float) -> GateResult`. FAIL if `unresolved_after > 0` or `drain_seconds > limit_s`.
  - `unknowns_seen_gate(hung: Optional[int], late_landed: Optional[int], resolved_by_recovery: Optional[int]) -> GateResult`. INCONCLUSIVE if `hung` or `late_landed` is 0, or `resolved_by_recovery` is 0.
  - `parked_gate(past_limit: Optional[int], parked_at_end: Optional[int]) -> GateResult`. FAIL on either above 0.
  - `quota_violations(max_rps: Dict[str, float], quota_rps: Dict[str, float]) -> List[str]`: one reason per account over quota. It's used as a validity reason, not a gate.
  - `callbacks_gate(sent: Optional[int], duplicated: Optional[int], distinct_recorded: Optional[int], rejected_503: Optional[int]) -> GateResult`. PASS only if `distinct_recorded == sent - duplicated`. The detail reports `rejected_503` but never fails on it.
  - `expiry_gate(backlog_start: Optional[int], backlog_end: Optional[int], expired_delta: Optional[int]) -> GateResult`. PASS if `backlog_end < backlog_start` and `expired_delta > 0`.
  - `grew(samples: List[float]) -> bool`: the Ck6Trend rule. `window = max(2, len // 4)`; `early = max(first window)`; `late = mean(last window)`; grew if `late > early * 1.5 + 10`.
  - `trend_gate(series: Dict[str, Optional[List[float]]], duration_s: int, min_duration_s: int) -> GateResult`. When `duration_s < min_duration_s` the trend isn't judged (spec §5), so it returns status `PASS` with detail `"reported only: run shorter than 1 hour"`. It doesn't return `NOT_APPLICABLE`, which would make the run `INCONCLUSIVE`. INCONCLUSIVE if any series is `None` or empty. FAIL naming every series that grew.
  - `verdict(correctness_ok: bool, valid: bool, gates: List[GateResult], stage1_passed: bool) -> str`, following the Global Constraints order.
- Any argument that is `None` makes that gate `INCONCLUSIVE` with detail `"<name> missing"`.

- [ ] **Step 1: Write the failing tests**, one per rule. Each test has its assertion as the name says:

```python
def test_drain_fails_when_unresolved_remain():      assert drain_gate(1, 120, 300).status == "FAIL"
def test_drain_fails_past_limit():                   assert drain_gate(0, 301, 300).status == "FAIL"
def test_drain_passes():                             assert drain_gate(0, 45, 300).status == "PASS"
def test_drain_missing_value_is_inconclusive():      assert drain_gate(None, 45, 300).status == "INCONCLUSIVE"
def test_no_hung_attempt_is_inconclusive():          assert unknowns_seen_gate(0, 7, 9).status == "INCONCLUSIVE"
def test_no_late_landing_is_inconclusive():          assert unknowns_seen_gate(5, 0, 9).status == "INCONCLUSIVE"
def test_parked_past_limit_fails():                  assert parked_gate(1, 0).status == "FAIL"
def test_parked_at_end_fails():                      assert parked_gate(0, 2).status == "FAIL"
def test_quota_over_names_account():                 assert quota_violations({"a": 101.0}, {"a": 100.0}) == ["recovery for a reached 101.0/s, over its 100.0/s quota"]
def test_callback_shortfall_fails():                 assert callbacks_gate(1000, 50, 949, 0).status == "FAIL"
def test_callback_all_recorded_passes():             assert callbacks_gate(1000, 50, 950, 3).status == "PASS"
def test_expiry_backlog_not_falling_fails():         assert expiry_gate(2_000_000, 2_000_000, 0).status == "FAIL"
def test_grew_matches_ck6trend():                    assert grew([10]*8 + [40]*8) and not grew([10]*16)
def test_trend_only_gates_one_hour_runs():           assert trend_gate({"rss": [1, 100]}, 600, 3600).status == "PASS"
def test_trend_names_growing_series():               assert "parked" in trend_gate({"parked": [0]*8 + [50]*8, "rss": [5]*16}, 3600, 3600).detail
def test_trend_empty_series_is_inconclusive():       assert trend_gate({"rss": []}, 3600, 3600).status == "INCONCLUSIVE"
def test_verdict_correctness_first():                assert verdict(False, False, [], True) == "FAIL"
def test_verdict_inconclusive_before_perf_fail():    assert verdict(True, True, [GateResult("x", "INCONCLUSIVE", "")], False) == "INCONCLUSIVE"
```

- [ ] **Step 2: Run to verify they fail:** `cd benchmarks && python3 -m pytest test_phase2.py -v`. Expected: the new tests FAIL with `ImportError`.
- [ ] **Step 3: Implement** the functions above in `benchmarks/phase2.py`.
- [ ] **Step 4: Run to verify they pass.** Expected: all pass, and Task 1's tests still pass.
- [ ] **Step 5: Commit** `bench: add the Stage 2 gates`.

### Task 3: Repeat summary (minimum of three)

**Files:**
- Modify: `benchmarks/phase2.py`, `benchmarks/test_phase2.py`

**Interfaces:**
- Consumes: each run's `result.json`, written by Task 5: `{"run_id", "step", "valid", "verdict", "completed_per_second", "p50_ms", "p99_ms", "error_rate_pct"}`.
- Produces: `summarize_repeats(results: List[dict], required: int) -> dict` returning `{"verdict", "runs", "min_completed_per_second", "max_completed_per_second", "worst_p99_ms", "worst_p50_ms", "reason"}`. INCONCLUSIVE if fewer than `required` runs or any run invalid, and the minimum is then `None`. Otherwise the verdict is the worst verdict among the runs, ordered FAIL > INCONCLUSIVE > PASS.
- Produces CLI: `python3 benchmarks/phase2.py summarize <run-dir>... --required 3`, printing a Markdown block for BASELINE and exiting 0 only on PASS.

- [ ] **Step 1: Write the failing tests:** `test_summary_needs_three_runs` (two PASS runs → INCONCLUSIVE, min `None`); `test_summary_rejects_invalid_run` (three runs, one `valid: False` → INCONCLUSIVE); `test_summary_takes_minimum_and_worst_p99` (rates 251.2, 249.8, 250.4 and p99 61, 88, 70 → min 249.8, max 251.2, worst p99 88); `test_summary_worst_verdict_wins` (PASS, FAIL, PASS → FAIL).
- [ ] **Step 2: Run to verify they fail.**
- [ ] **Step 3: Implement** `summarize_repeats` and the `summarize` subcommand (argparse) in `phase2.py`.
- [ ] **Step 4: Run to verify they pass.**
- [ ] **Step 5: Commit** `bench: summarize Stage 2 repeats by their minimum`.

### Task 4: Evidence collection and the phase2 path in `run.sh`

**Files:**
- Create: `benchmarks/phase2_collect.py`, `benchmarks/test_phase2_collect.py`, `benchmarks/seed-expirable.sql`
- Modify: `benchmarks/run.sh`, `tools/OpenPayments.MockPsp/AttemptStore.cs`, `tests/OpenPayments.MockPsp.Tests/Stage2MockTests.cs`

**Interfaces:**
- Consumes: the mock knobs and callbacks from Stage 2 Task 24. These metrics from Tasks 26, 27 and 30: `op_psp_events_parked`, `op_keys_expirable_backlog`, `op_keys_expired_total`, the reconciler's per-account recovery-call counter, and the reconciler's health endpoint. Task 27 names the last two; if it named none, stop and escalate.
- Produces: the mock's `/admin/stats` gains `hung`, `late_landed`, `callbacks_sent` and `callbacks_duplicated` counters.
- Produces: `phase2_collect.build_evidence(sql: Callable[[str], List[tuple]], mock_before: dict, mock_after: dict, prom_range: Callable[[str], Optional[List[float]]], drain_seconds: Optional[float], start_iso: str, end_iso: str) -> dict`, written by `main()` as `<run-dir>/phase2.json`. Keys: `unresolved_after`, `drain_seconds`, `hung`, `late_landed`, `resolved_by_recovery`, `resolved_by_callback`, `resolution_age_s` (`{p50, p99, max}`), `parked_past_limit`, `parked_at_end`, `callbacks_sent`, `callbacks_duplicated`, `callbacks_distinct_recorded`, `callbacks_rejected_503`, `recovery_max_rps` and `recovery_quota_rps` (both per account), `expiry_backlog_start`, `expiry_backlog_end`, `keys_expired_delta`, `commits_per_payment`, `series` (unresolved, parked, rss, gen2_gc, threadpool_queue, pool_wait, each a list of 10 s samples). A failed query or empty series gives `None`, never 0.
- SQL the collector runs. These are design decisions, written here exactly; `:start` and `:end` are the window:
  - unresolved after: `select (select count(*) from payment_attempts where state in ('PENDING','UNKNOWN')) + (select count(*) from refunds where state in ('PENDING','UNKNOWN'))`
  - resolved by recovery: attempts and refunds created in the window with `recovery_attempts > 0` and state not in `('PENDING','UNKNOWN')`
  - resolved by callback: distinct `attempt_id` and `refund_id` in `psp_events` with `source = 'callback' and outcome = 'applied'` and `received_at` in the window
  - resolution age: `extract(epoch from k.resolved_at - a.created_at)` for those attempts, joined `payments p on p.id = a.payment_id`, `idempotency_keys k on k.merchant_id = p.merchant_id and k.key = a.idempotency_key`. Report p50, p99 and max with `percentile_cont`.
  - parked past limit: `count(*) from psp_events where outcome = 'expired' or (processed_at is null and received_at < now() - interval '15 minutes')`. Parked at end: `count(*) where processed_at is null`.
  - distinct callbacks recorded: `count(*) from psp_events where source = 'callback' and received_at >= :start`
- `seed-expirable.sql`, with psql variable `count`: inserts `count` keys spread over all merchants, `resolved_at = now() - interval '25 hours' - (n * interval '1 ms')`, `on conflict do nothing`.
- `run.sh`, for `--stage phase2` only:
  1. Refuse to start (exit 4) unless the reconciler answers its health endpoint (`RECONCILER_URL`, default `http://127.0.0.1:8090/health`).
  2. PUT `phase2.json`'s `mock` object to `/admin/config`, GET it back, and exit 4 naming the first mismatching knob.
  3. Run `seed-expirable.sql` with `expiry_prefill_keys`.
  4. Save `/admin/stats` as `mock-stats-before.json`.
  5. After k6, replace the fixed `sleep` with a poll every 5 s of the unresolved query plus `count(*) from psp_events where processed_at is null`, until both are 0 or `drain_limit_seconds` passes. Write the seconds taken (or `null` if the limit passed) to `drain.json`.
  6. Save `mock-stats-after.json`, copy the effect log, then run `phase2_collect.py`, then op-checks as today.
  7. Phase0 and phase1 behaviour is unchanged.

- [ ] **Step 1: Write the failing C# test** `Stats_count_hung_late_and_callbacks` in `Stage2MockTests.cs`: with `approve_then_hang_rate` 1.0 one sale makes `hung` 1; with `late_landing_rate` 1.0 it makes `late_landed` 1; with `callback_rate` 1.0 and `callback_duplicate_rate` 1.0 it makes `callbacks_sent` 2 and `callbacks_duplicated` 1.
- [ ] **Step 2: Write the failing Python tests** in `test_phase2_collect.py`, using fake `sql` and `prom_range`: `test_failed_query_gives_none_not_zero`, `test_empty_series_gives_none` (covers Review Focus 2), `test_mock_counters_are_after_minus_before` (before `hung` 3, after 10 → 7), `test_resolution_age_reads_p50_p99_max`.
- [ ] **Step 3: Run to verify they fail:** `dotnet test tests/OpenPayments.MockPsp.Tests --filter Stats_count_hung_late_and_callbacks` and `cd benchmarks && python3 -m pytest test_phase2_collect.py -v`.
- [ ] **Step 4: Implement** the counters, `phase2_collect.py`, `seed-expirable.sql` and the `run.sh` changes.
- [ ] **Step 5: Run to verify they pass,** then the shell checks for Review Focus 1, 3 and 4 on the local stack:
  - Reconciler stopped: `benchmarks/run.sh 0 --env local --stage phase2` exits 4 with `reconciler is not running`.
  - Mock not set to the profile: run a phase1 step first, so the mock holds `callback_rate` 0. Then run `PHASE2_SKIP_APPLY=1 benchmarks/run.sh 0 --env local --stage phase2`, where the switch skips only the PUT. It exits 4 naming the first mismatching knob.
  - Seed twice on the same database: `psql ... -v count=1000 -f benchmarks/seed-expirable.sql` twice leaves the expirable count up by exactly 1,000.
  - Reconciler up: `benchmarks/run.sh 0 --env local --stage phase2` completes and writes `phase2.json`, `drain.json` and both mock stats files.
- [ ] **Step 6: Commit** `bench: collect Stage 2 evidence and drain in run.sh`.

### Task 5: Stage 2 gates in `report.py`

**Files:**
- Modify: `benchmarks/report.py`, `benchmarks/test_report.py`

**Interfaces:**
- Consumes: `phase2.json` (Task 4), `phase2.py` gates (Task 2), `profiles/phase2.json` thresholds (Task 1).
- Produces: `required_checks("phase2")` adds `CK7` and `CK8`. `write_result` also writes `result.json` with the fields Task 3 consumes, plus `"stage2_gates": [{name, status, detail}]`.
- For phase2:
  - `result.md` gains a "Stage 2 gates" table, one row per gate.
  - It gains a "Stage 2 measures" block: callback req/s, recovery calls/s, parked events, keys expired/s, expirable backlog start and end, unresolved at end, drain time, resolution age p50, p99 and max, resolved by recovery and by callback, and commits per payment against 4.2.
  - Any `quota_violations` reason is added to validity.
  - The exit code follows `verdict()`.
  - The header stays preview.
  - A 1-hour run (`duration_seconds` ≥ `trend_min_duration_seconds`) also lists the 5-minute window table of p99, unresolved and parked.

- [ ] **Step 1: Write the failing tests** in `test_report.py`. These include Task 36's four:

```python
def test_phase2_run_fails_if_unresolved_remain_after_drain(tmp_path): ...  # phase2.json unresolved_after 3 → exit 1, "Drain | FAIL" in result.md
def test_phase2_run_fails_if_any_event_parked_past_limit(tmp_path): ...   # parked_past_limit 1 → exit 1
def test_recovery_calls_over_quota_invalidates_run(tmp_path): ...         # recovery_max_rps {"a": 120} quota {"a": 100} → "invalid" and the reason in result.md
def test_phase2_is_labelled_preview(tmp_path): ...                        # env linux, stage phase2 → header "preview"
def test_phase2_requires_ck7_and_ck8(tmp_path): ...                       # checks.json without CK8 → not PASS
def test_phase2_missing_evidence_is_inconclusive(tmp_path): ...           # no phase2.json → result.json verdict "INCONCLUSIVE"
def test_phase1_result_unchanged(tmp_path): ...                           # an existing phase1 fixture renders with no Stage 2 section
```

- [ ] **Step 2: Run to verify they fail:** `cd benchmarks && python3 -m pytest test_report.py -v`.
- [ ] **Step 3: Implement** in `report.py`, calling `phase2.py`. Keep Stage 1 rendering byte-identical for phase0 and phase1.
- [ ] **Step 4: Run** `cd benchmarks && python3 -m pytest -v`. Expected: every test in `test_report.py`, `test_phase2.py`, `test_phase2_collect.py` and `test_bench_page.py` passes.
- [ ] **Step 5: Commit** `bench: judge phase2 runs by the Stage 2 gates`.

### Task 6: Run the tests and record them

**Files:**
- Modify: `benchmarks/BASELINE.md`, `benchmarks/README.md`, `benchmarks/report-runs.json`, `docs/gates/stage-2-progress.md`
- Create: `benchmarks/runs/<run-id>/*` for each run

- [ ] **Step 1: README.** Add a "Stage 2 payments/s tests" section with the two commands, the reconciler requirement, and the summarize command.
- [ ] **Step 2: Run step 200 three times,** each after a [full benchmark reset](../../../benchmarks/README.md#full-benchmark-reset), on a clean tree: `benchmarks/run.sh 200 --env local --stage phase2`.
  - An invalid run is rerun from a reset and never counted.
  - A drain, parked-event or callback failure is a build defect: stop, fix it in its own commit, and redo all three.
- [ ] **Step 3: Summarize:** `python3 benchmarks/phase2.py summarize benchmarks/runs/<id1> <id2> <id3> --required 3`.
- [ ] **Step 4: Run step 201 once** from a reset: `benchmarks/run.sh 201 --env local --stage phase2`.
- [ ] **Step 5: Record.**
  - Add a "Stage 2 payments/s tests (preview)" section at the top of `BASELINE.md`, in the D56 section's form. It has the run IDs, the summary block, the Stage 2 measures, and the 5-minute table for the hour.
  - If p99 failed, name the bottleneck with its wait-event evidence (`WALWriteLock`, `SyncRep`, `WalSync`), state the next step under [D64](../../decisions.md#d64), and raise an escalation. Don't tune anything to make the run pass.
  - Add the run IDs to `report-runs.json`.
  - Add a progress line for Task 36 Step 3b in `stage-2-progress.md`.
- [ ] **Step 6: Commit** `test: record Stage 2 payments/s previews`.

---

## Spec coverage

| Spec section | Task |
|---|---|
| §1 Decisions, §2 Runs | 1 (profiles), 6 (runs) |
| §3 Phase2 profile, expiry prefill | 1, 4 |
| §4 Sequence | 4 |
| §5 Stage 1 gates | 5 (unchanged code path, CK7/CK8 added) |
| §5 Stage 2 gates | 2, 4, 5 |
| §5 Minimum of three | 3 |
| §6 Report additions | 5 |
| §7 When a gate fails | 6 |
| §8 Changes | 1-6 |
