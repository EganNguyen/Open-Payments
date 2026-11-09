# First ramp baseline

## D47 laptop limit search (2026-11-09)

Status: **local preview only.** No `C` or `N` comes from this ([E4](../docs/gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4)). It replaces the old-mix rows below for comparison.

| Item | Value |
|---|---|
| Stack | Docker Compose: one API instance, PostgreSQL 18.6 primary with two quorum standbys, PgBouncer 1.26.1, one mock PSP (200 ms, 3% declines) |
| Mix | [D47](../docs/decisions.md#d47), `TWO_CALL_SHARE=0.2`, `--stage phase1` |
| Start | [Full benchmark reset](README.md#full-benchmark-reset) at 04:28 UTC, then steps in order with data accumulating |
| Harness | Report fixes `4540c23` (error rate), `18d0f63` (E8 share bound), `3faaaf6` (latency without `setup()`) |

| Step | Target req/s | Admitted req/s | Completed payments/s | Platform p50 | p95 | p99 | API CPU | Payments after | Result |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 1 | 1.09 | 0.64 | 20.41 ms | 28.15 ms | 30.35 ms | 1.0% | 399 | pass, valid |
| 1 | 10 | 9.98 | 5.09 | 15.40 ms | 23.01 ms | 26.46 ms | 6.5% | 1,958 | pass, valid |
| 3 | 100 | 99.16 | 49.81 | 6.79 ms | 9.72 ms | 12.57 ms | 34.3% | 32,270 | pass, valid |
| 5 | 500 | 491.98 | 248.32 | 6.07 ms | 12.07 ms | 17.74 ms | 117.9% | 184,428 | pass, valid |
| 6 | 1,000 | 988.84 | 498.33 | 12.21 ms | 28.73 ms | 58.43 ms | 194.3% | 639,315 | gates pass, **invalid** |

Unexpected errors were 0.000% at every step. CK1-CK6, CK9 and CK10 passed at every step; CK7 and CK8 start in Phase 2. Pool wait was 0.0 s and deadlocks 0 throughout.

**The search stopped at step 6,** the first invalid run, as E4 requires. k6 dropped 68 of 902,428 arrivals, 0.0075%. Generator CPU was 1.9%, and k6 used at most 305 of its 800 VUs, so the drops happened while it added VUs past the 300 it pre-allocated. That is a limit of the generator setup on this laptop, not a system bottleneck, and no bottleneck is named from it. Step 6's offered load is D51's laptop milestone of 500 completed payments/s, but one invalid run doesn't meet D51's three valid 10-minute windows.

**What the resources show, without naming a bottleneck:** API CPU grew with load, to about 1.9 cores at step 6. p99 rose from 17.74 ms at step 5 to 58.43 ms at step 6, still within the 100 ms gate. Database CPU stayed below half a core.

Runs: `20261109T043519Z-3faaaf6-s0-14c1`, `20261109T044032Z-3faaaf6-s1-005e`, `20261109T044543Z-3faaaf6-s3-7dce`, `20261109T045857Z-e4bff52-s5-47d3`, `20261109T050918Z-786d8ef-s6-0bba`. Earlier attempts on 2026-11-09 are excluded: `…040109Z-3d728c1-s0-3b3c` sent no API token (every request 401), `…040758Z-4540c23-s0-2958` ran before the reset (count mismatch, E8), and `…042813Z-18d0f63-s0-2291` counted `setup()` latency (E8).

## Old-mix baseline (2026-11-06)

Status: **local preview only**. This is a laptop run for comparison, not a capacity result.
No `C` is recorded from it, and no number here should be used for capacity sizing.

These runs predate [D47](../docs/decisions.md#d47): half their payments are authorized and never
captured, and every one counts as completed. The ramp profile in them was also later corrected.
They must be replaced by a rerun of steps 0-3 at the new mix.

### Configuration

| Item | Value |
|---|---|
| Date | 2026-11-06 |
| Code at run time | `55184a8` |
| Environment | 18-core, 48 GB MacBook with Docker given 18 CPUs and 40 GB |
| Stack | One API instance, one PostgreSQL 18.6 primary with two synchronous standbys, PgBouncer 1.26.1, one mock PSP |
| Database config | `PG_CONFIG=primary.macbook.conf` for the primary and both standbys; `scripts/check-db-settings.sh` passed |
| PSP | Fixed 200 ms latency, 0 ms jitter, 3% declines, 0 errors, 0 callbacks |
| Mix | 60% payment create/authorize, 15% status, 10% refund, 10% retry, 5% other; 50% auto-capture |
| Workload model | k6 `ramping-arrival-rate`; each 5- or 10-minute stage ramps from 1 req/s to the step target, so the average is about half the target |
| Evidence | The four `benchmarks/runs/` directories listed below |

The two standbys use the same config file and set `STANDBY_SHARED_BUFFERS=3GB` in
`start-standby.sh`; the primary keeps the file's 10 GB value. Both standbys were healthy
and in `quorum` state before migrations ran.

### Results

| Step | Target req/s | Admitted req/s | Completed payments/s | Platform p50 | Platform p95 | Platform p99 | End-to-end p99 | Gates |
|---:|---:|---:|---:|---:|---:|---:|---:|---|
| 0 | 1 | 1.00 | 0.29 | 18.38 ms | 24.32 ms | 28.31 ms | 228.31 ms | pass |
| 1 | 10 | 5.50 | 1.59 | 17.28 ms | 23.12 ms | 24.97 ms | 224.97 ms | pass |
| 2 | 50 | 25.48 | 7.53 | 10.32 ms | 18.79 ms | 22.02 ms | 222.02 ms | pass |
| 3 | 100 | 50.48 | 14.56 | 8.78 ms | 15.01 ms | 20.08 ms | 220.08 ms | pass |

Platform latency is end-to-end latency minus the fixed 200 ms PSP delay. The unexpected
error rate was 0.000% at every step. CK1-CK5, CK9 and CK10 passed at every step; CK6 was
not applicable because the local stack has no Prometheus, and CK7/CK8 start in Phase 2.

- Step 0: `20261106T042621Z-55184a8-s0-5de5` ([result](runs/20261106T042621Z-55184a8-s0-5de5/result.md))
- Step 1: `20261106T043213Z-55184a8-s1-0ad4` ([result](runs/20261106T043213Z-55184a8-s1-0ad4/result.md))
- Step 2: `20261106T043810Z-55184a8-s2-701a` ([result](runs/20261106T043810Z-55184a8-s2-701a/result.md))
- Step 3: `20261106T044402Z-55184a8-s3-00a5` ([result](runs/20261106T044402Z-55184a8-s3-00a5/result.md))

### Bottlenecks and limits

No gate failed at steps 0-3, so no bottleneck was identified from these runs. The run
harness did not record API CPU, memory, database CPU, WAL rate, pool wait or lock waits
(`n/a` in the results), so the absence of a failed gate is not evidence that those
resources were idle. The generator CPU stayed at or below 0.1%.

Step 3 is the first capacity test, but the ramp delivered an average of 50.48 req/s, not
the 100 req/s target. That is the profile's starting rate, not a result for 100 req/s.

### What this does not establish

- There is no `C` and no `N`. Steps 4 and 5 need a Linux host, and they have not run.
- No statement about 5,000 completed payments/s is supported by a laptop run. The target is
  not assessed here: with no `C`, no `N` can be computed and no reachability claim is made.
- The measured commits-per-payment comparison with the Task 6 estimate is not possible without
  the Linux steps 4-5.
- D46 defers idempotency-key expiry to Stage 2. `idempotency_keys` only grows during
  these runs; V7 must be measured at Stage 2's design gate before that job is built.
