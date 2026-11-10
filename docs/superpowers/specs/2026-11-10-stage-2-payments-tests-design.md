# Stage 2 payments/s tests: 10 minutes and 1 hour

**Status:** design, approved by the owner in chat on 2026-11-10. Written before any run; the pass conditions below are fixed before the first run, as [D56](../../decisions.md#d56) requires.

**Goal:** prove that Stage 2's recovery machinery holds under sustained load on the laptop. Every unknown outcome must resolve while load runs, and nothing may build up over an hour, with the same latency and correctness gates as Stage 1.

**Where it fits:** this adds Step 3b to [Task 36](../plans/2026-11-10-stage-2-psp-and-recovery.md#task-36-benchmarks-steps-0-5-with-recovery-and-g2). It runs after steps 0-5 and before G2. It uses the mock PSP only. **No Stripe account is needed:** Stripe's sandbox allows 25 requests/s per account and Stripe discourages load tests there ([Stage 2 design gate](../../gates/stage-2-design.md)), so the sandbox proves behaviour, never rate.

## 1. Decisions

| Item | Decision | Why |
|---|---|---|
| Rates | 500 req/s for 10 minutes; 250 req/s for 1 hour | The Stage 1 soak passed at 250 req/s and failed at 500 on WAL commit stalls ([BASELINE](../../../benchmarks/BASELINE.md)). These runs show whether Stage 2's added commits keep both inside the gates |
| PSP behaviour | The steady phase2 mix (§3), with no kills or restarts | Kills and restarts belong to the reliability scenarios (C4, C54, C26). This test proves that recovery keeps up with a steady flow of unknowns |
| Harness | `run.sh`, `report.py` and the k6 D47 mix, extended | Task 36 already changes these files. Results stay comparable with D56, and there's no second harness to disagree with this one. The OP-BENCH orchestrator is unbuilt, and this doesn't wait for it |
| Label | `PREVIEW` | Laptop evidence ([D53](../../decisions.md#d53)). Nothing here ticks a row in [Verification](../../verification.md) |

## 2. Runs

| | 10-minute | 1-hour |
|---|---|---|
| Step ID | 200 | 201 |
| Offered rate | 500 req/s, [D47](../../decisions.md#d47) mix: about 250 completed payments/s | 250 req/s: about 125 completed payments/s |
| Duration | Ramp, then 600 s measured | Ramp, then 3,600 s measured |
| Repeats | 3, each from a [full benchmark reset](../../../benchmarks/README.md#full-benchmark-reset). The result is the minimum of the three, with the spread ([D51](../../decisions.md#d51)'s rule) | 1, from a full reset |
| Deployables | 1 API instance, 1 reconciler (also running timers, the parked-event worker and expiry), 1 mock PSP | Same |
| Command | `benchmarks/run.sh 200 --env local --stage phase2` | `benchmarks/run.sh 201 --env local --stage phase2` |

The measured window starts once the ramp reaches the rate. `setup()` traffic is excluded, as in D51.

## 3. The phase2 mock profile

`benchmarks/profiles/phase2.json` holds these values. `run.sh --stage phase2` applies them to the mock before the run.

| Knob | Value | Reason |
|---|---|---|
| `latency_ms` | 200 | Same as every earlier run, so overhead compares |
| `error_rate` | 0.02 | Lower end of L1's 2-5% band |
| `approve_then_hang_rate` | 0.005 | A continuous source of unknowns where the PSP acted (L2/L4) |
| `late_landing_rate` | 0.005 | Replay that finds a late effect |
| `callback_rate` | 1.0 | Every effect calls back: D47's upper bound of 4.2 commits per payment |
| `callback_duplicate_rate` | 0.05 | Dedupe on `psp_event_id` under load |
| `callback_reorder_window_ms` | 500 | Out-of-order events, so the parked-event path runs |
| `rate_limit_per_s` | 1,000 | Mock default; recovery's quota is 10% of it ([D62](../../decisions.md#d62)) |

**Expected load, by arithmetic ([D64](../../decisions.md#d64)):**

- **PSP calls:** about 70% of requests reach the PSP, so about 350 PSP calls/s at 500 req/s and 175 at 250.
- **Unknowns:** 1% of those calls become unknowns: about 3.5/s and 1.75/s. The recovery quota is 100 calls/s, so it isn't the limit.
- **Callbacks:** about one per PSP effect arrives at the API, about 350/s at 500 req/s. They come from the mock, not k6, so they aren't arrivals and are reported on their own line.
- **Commits:** commits per payment rise from 2.8 to 4.2, so the WAL flush rate rises by about half. That is the laptop's known limit, and the most likely place for these runs to fail.

**Key expiry** runs throughout. Retention is 24 hours, so no key expires naturally inside a run. The reset therefore prefills an expirable backlog of resolved keys with `resolved_at` older than 24 hours: 2,000,000 keys, spread over the 16 partitions. The rate cap starts at twice key creation ([Stage 2 design gate](../../gates/stage-2-design.md)), at most about 500 deletes/s at 250 req/s, or 1.8 million in an hour. So expiry is still deleting at the end of both runs, and its cost is in every measured window.

## 4. Sequence of one run

1. Full reset, then prefill the expiry backlog, then check that the tree is clean.
2. Check that the API, the reconciler and the mock are running; refuse to start if the reconciler isn't. Apply the phase2 profile and read it back from the mock.
3. k6 ramps to the rate and holds it for the window. Samplers record every 10 s (§6).
4. k6 stops. **Drain:** the mock keeps sending callbacks and the reconciler keeps working, until unresolved attempts, unresolved refunds and parked events are all 0, or 5 minutes pass.
5. Copy the effect log after the drain, then run `op-checks --stage phase2`: CK1-CK10, with CK5 using its timeouts and CK7 and CK8 applicable.
6. `report.py` writes `result.md` with every gate in §5.

## 5. Gates

Each gate has its own result: `PASS`, `FAIL`, `INCONCLUSIVE` or `NOT_APPLICABLE`. A required gate that is `NOT_APPLICABLE` makes the run `INCONCLUSIVE`. The numbers go in `benchmarks/profiles/phase2.json` under `thresholds` before the first run.

**Stage 1 gates, unchanged:**

| Gate | Pass |
|---|---|
| Platform overhead | p50 ≤ 25 ms, p99 ≤ 100 ms over the window. Computed as `report.py` does today, as k6 end-to-end minus the configured 200 ms, which is valid because mock jitter is 0 |
| Error rate | < 0.1%, excluding 402, 409, 429, 503 |
| Validity | No dropped arrivals, generator CPU ≤ 80%, mock adds ≤ 5 ms at p99, mix shares within 2 pp |
| Throughput count | DB count of completed payments and `op_payments_completed_total` agree within 0.5% |
| Correctness | CK1-CK10 all pass |

**Stage 2 gates, new:**

| Gate | Pass | Source |
|---|---|---|
| Drain | Unresolved attempts and refunds reach 0 within 5 min of k6 stopping. The limit is the 30 s attempt timeout plus several lease cycles; both mock fault kinds resolve by replay with the same key, so nothing legal is left | DB after drain |
| Unknowns seen | At least one approve-then-hang and one late landing was resolved in the window. A run with none hasn't tested recovery, so it is `INCONCLUSIVE` | DB, mock effect log |
| Resolution age | p50, p99 and max from attempt creation to `resolved_at`, for every unknown in the window. Reported, not gated; the drain gate bounds the maximum | DB |
| Parked events | 0 past the 15-minute limit; 0 parked at the end of the drain | `psp_events`, CK5 |
| Recovery quota | Recovery calls/s per account never over quota; over quota makes the run invalid | Reconciler metrics |
| Callbacks | Every callback the mock sent ends processed or deduped; callback 503s are counted and reported | Mock log, `psp_events` |
| Expiry | The expirable backlog is lower at the end than at the start; `op_keys_expired_total` rises | Expiry metrics |
| Trends, 1-hour only | No upward slope, by the `Ck6Trend` method, in unresolved backlog, parked count, RSS, Gen 2 GC/s, ThreadPool queue and pool wait | Samplers |

A 10-minute run is too short for a slope to mean anything, so the 10-minute runs report the same series without gating them.

**Verdict order** (as in the [benchmark suite](2026-11-09-benchmark-suite-design.md#7-gates-and-verdict)): a correctness gate fails → `FAIL`; any run invalid, value missing or required gate `INCONCLUSIVE` → `INCONCLUSIVE`; any performance or Stage 2 gate missed → `FAIL`; otherwise `PASS`. Always labelled `PREVIEW`.

## 6. Report additions

`result.md` adds Task 36's fields:

- callback req/s
- parked events
- recovery calls/s
- keys expired/s and the expirable backlog
- unresolved count at the end
- commits per payment against D47's 4.2 with callbacks

It also adds:

- the drain time
- resolution age p50, p99 and max
- unknowns resolved by recovery and by callback
- each gate in §5 with its own result

The 1-hour run also lists the 5-minute window table that BASELINE's soaks use.

Results go in a **Stage 2** section of `benchmarks/BASELINE.md`, with run IDs, in the same form as the D56 section.

## 7. When a gate fails

- **p99 over 100 ms with `WALWriteLock`, `SyncRep` or `WalSync` waits:** record it as the bottleneck with that evidence. Under D64, that is when a measurement runs to find the cause, including V7 if expiry is implicated. Asynchronous replication, `synchronous_commit` below `on`, `fsync = off` and RAM disks stay ruled out ([D57](../../decisions.md#d57)).
- **Drain, parked-event or callback gate fails:** that is a correctness defect in the Stage 2 build, not a laptop limit. Fix it and rerun all repeats.
- **Invalid run:** rerun from a reset; never average an invalid run in.
- **A builder doesn't change a threshold or rate after seeing a result.** A change goes back to design review.

## 8. Changes

| File | Change |
|---|---|
| `benchmarks/profiles/steps.json` | Steps 200 (500 req/s, 600 s) and 201 (250 req/s, 3,600 s) |
| `benchmarks/profiles/phase2.json` | New: mock knobs (§3), expiry prefill size, `thresholds` (§5) |
| `benchmarks/run.sh` | Apply the phase2 profile, prefill expiry, start the reconciler, wait for the drain, copy the effect log after the drain |
| `benchmarks/report.py` | The Stage 2 gates, the trend gate for 1-hour runs, the minimum-of-three rule, the report fields |
| `benchmarks/test_report.py` | Task 36's four tests, plus tests for the drain limit, "unknowns seen", the trend gate only on 1-hour runs, the minimum-of-three rule, and an `INCONCLUSIVE` result when a value is missing |
| `benchmarks/README.md` | How to run steps 200 and 201 |
| `benchmarks/BASELINE.md` | The Stage 2 section, after the runs |
| [Stage 2 plan](../plans/2026-11-10-stage-2-psp-and-recovery.md), Task 36 | Step 3b pointing here |

## 9. Not covered

- **Capacity.** These are fixed-rate previews, not a limit search. `C` and `N` stay with the first Linux run (D53).
- **Faults beyond the steady mix:** kills, restarts and backlogs of thousands (C4, C26, C47, C54) keep their own scenarios.
- **Stripe:** the sandbox's only rate test is G2's 60-second run per account (Task 36 Step 4).
- **Two API instances or two reconcilers** under this load.
