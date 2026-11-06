# First ramp baseline

Status: **local preview only**. This is a laptop run for comparison, not a capacity result.
No `C` is recorded from it, and no number here should be used for capacity sizing.

## Configuration

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

## Results

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

## Bottlenecks and limits

No gate failed at steps 0-3, so no bottleneck was identified from these runs. The run
harness did not record API CPU, memory, database CPU, WAL rate, pool wait or lock waits
(`n/a` in the results), so the absence of a failed gate is not evidence that those
resources were idle. The generator CPU stayed at or below 0.1%.

Step 3 is the first capacity test, but the ramp delivered an average of 50.48 req/s, not
the 100 req/s target. That is the profile's starting rate, not a result for 100 req/s.

## What this does not establish

- There is no `C` and no `N`. Steps 4 and 5 need a Linux host, and they have not run.
- No statement about 5,000 completed payments/s is supported by a laptop run.
- D46 defers idempotency-key expiry to Stage 2. `idempotency_keys` only grows during
  these runs; V7 must be measured at Stage 2's design gate before that job is built.
