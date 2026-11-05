# Mock PSP Capacity

Task 8, Step 5: show whether the mock PSP can carry 25,000 requests/s with 200 ms modelled latency and less than 5 ms added p99, and record what the laptop actually reached.

**Verdict: the laptop reached a valid 10,000 req/s preview at 1.34 ms added p99. The 25,000 req/s claim is not proven.** Every 25,000 req/s attempt on this laptop dropped arrivals or returned errors, which makes the run invalid under the [trust rules](../docs/superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#when-a-result-can-be-trusted). A benchmark-class host must repeat this before a Linux run is trusted, as Task 8 requires.

## Environment

| Item | Value |
|---|---|
| Machine | MacBook Pro, Apple M5 Pro, 18 cores, 48 GB |
| OS | macOS (host) |
| .NET | SDK 10.0.403, Debug build |
| k6 | v2.3.0 (darwin/arm64) |
| Mock | `tools/OpenPayments.MockPsp`, one process unless a row says sharded |
| Mock config | `latency_ms=200, jitter_ms=0, rate_limit_per_s=100000, decline_rate=0, error_rate=0, callback_rate=0` |
| Effect log | Every request uses a unique idempotency key, so every request writes one effect. |
| Valid runs | Clean git tree, no dropped iterations, generator below 80% CPU, configuration above. |

Generator CPU is from `/usr/bin/time -l` (`user + sys` divided by wall time). It is reported as a share of the 18-core machine, not of one core. The 10,000 req/s run used about 1.25 cores (about 7% of the machine); the 25,000 req/s run used about 3.1 cores (about 17%). The generator did not exceed the 80% rule on any run.

**Added latency** is `http_req_duration - 200 ms`, the mock's own time over the configured delay. For a run with failed or timed-out requests, the p99 is not meaningful because failed requests record almost no duration; those rows are marked invalid.

## Runs

Commands are from the repository root. `RATE`, `HOLD`, and `VUS` are environment variables read by the scripts.

| # | Commit | Script | Offered req/s | Achieved req/s | Dropped iterations | Added p99 | Failed requests | Trust |
|---|---|---|---|---|---|---|---|---|
| 1 | Task 8 worktree | `mock-psp-capacity.js`, 30 s ramp + 30 s hold | 25,000 | 18,686 | 14,958 | 29.97 ms | 4,393 (0.39%) | Invalid: dropped arrivals, dirty tree |
| 2 | Task 8 worktree | same, quiet logging | 25,000 | 18,854 | 4,170 | 32.90 ms | 819 (0.07%) | Invalid: dropped arrivals, dirty tree |
| 3 | Task 8 worktree | 10 s ramp + 30 s hold | 5,000 | 4,473 | 130 | 1.24 ms | 0 | Invalid: dropped arrivals, dirty tree |
| 4 | Task 8 worktree | 10 s ramp + 30 s hold | 10,000 | 8,811 | 610 | 1.45 ms | 0 | Invalid: dropped arrivals, dirty tree |
| 5 | Task 8 worktree | 10 s ramp + 30 s hold | 15,000 | 13,093 | 1,122 | 1.83 ms | 2,013 (0.38%) | Invalid: dropped arrivals, dirty tree |
| 6 | Task 8 worktree | fixed VUs, constant rate, 30 s | 25,000 | 24,752 | 2,372 | 91.89 ms | 4,147 (0.55%) | Invalid: dropped arrivals, dirty tree |
| 7 | Task 8 worktree | fixed VUs, constant rate, 30 s | 5,000 | 4,954 | 361 | 3.58 ms | 0 | Invalid: dropped arrivals, dirty tree |
| 8 | Task 8 worktree | fixed VUs, constant rate, 30 s | 10,000 | 9,933 | 0 | 1.22 ms | 0 | Invalid: dirty tree |
| 9 | Task 8 worktree | fixed VUs, constant rate, 30 s | 15,000 | 14,877 | 662 | 2.16 ms | 4,105 (0.91%) | Invalid: dropped arrivals, dirty tree |
| 10 | Task 8 worktree | three shards, 25 s | 25,000 | 1,501 | 665,680 | 25.76 s | 42,634 (50.54%) | Invalid: dropped arrivals, 50% errors |
| 11 | `dac68e8` | fixed VUs, constant rate, 60 s | 10,000 | 9,592 | 22,526 | 4.65 ms | 4,387 (0.75%) | Invalid: dropped arrivals |
| 12 | `c7c8a21` | fixed VUs (3,000), constant rate, 30 s | 10,000 | 9,907 | 0 | 1.79 ms | 25 (0.00%) | Valid preview |
| 13 | `c7c8a21` | fixed VUs (7,500), constant rate, 30 s | 25,000 | 24,778 | 1,584 | 26.32 ms | 1,808 (0.24%) | Invalid: dropped arrivals |
| 14 | `c7c8a21` | three shards, 30 s | 25,000 | 1,239 | 675,640 | 34.62 s | 43,521 (58.52%) | Invalid: dropped arrivals, 58% errors |
| 15 | `99970c0` | fixed VUs (7,500), constant rate, 30 s | 25,000 | 24,765 | 1,990 | 28.77 ms | 939 (0.12%) | Invalid: dropped arrivals |
| 16 | `99970c0` | fixed VUs (6,000), constant rate, 30 s | 20,000 | 19,844 | 629 | 21.36 ms | 180 (0.03%) | Invalid: dropped arrivals |
| 17 | `99970c0` | fixed VUs (4,500), constant rate, 30 s | 15,000 | 14,896 | 108 | 10.09 ms | 48 (0.01%) | Invalid: dropped arrivals |
| 18 | `99970c0` | fixed VUs (3,000), constant rate, 30 s | 10,000 | 9,933 | 0 | 1.34 ms | 0 | Valid preview |

Run 18 is the clean-tree result on the final code and holds the mock at 10,000 req/s with 1.34 ms added p99. Run 12 is the same shape before effect-log batching. Both are previews: they are from a laptop, and the spec does not accept a laptop result as proof.

## What the numbers say

- **At 10,000 req/s the mock is not the limiting component on this laptop.** Added p99 is 1.34 ms, well under 5 ms, and the run has no dropped arrivals and no failed requests.
- **At 25,000 req/s the laptop cannot produce a valid run.** The generator drops arrivals even with fixed VUs, and added p99 is 10-30 ms. The failures and drops rise as the offered rate rises, so the mock's capacity at 25,000 req/s is unproven, not disproven.
- **The three-shard attempts failed on tooling, not on a measured shard limit.** All three shards returned 50% or more errors and Kestrel logged socket timeouts, while the offered rate fell to 1.2-1.5k req/s. That is an invalid result and says nothing about sharded mock capacity. It needs a clean rerun on benchmark hardware, with one generator per shard or a generator that is shown not to be the limit.
- **Effect-log batching did not change the 25,000 req/s verdict.** It reduced the 10,000 req/s added p99 from 1.79 ms to 1.34 ms, but the 25,000 req/s run still dropped arrivals.

## Not proven / next action

- The 25,000 req/s, under-5 ms claim is open.
- Before any Linux run is trusted, repeat run 18 and the 25,000 req/s run on benchmark-class hardware, with the generator on a separate machine and the mock shown not to be the limit. If one instance falls short there, shard by key hash and record the result.
- The laptop result does not block the previews in Task 17; those are labelled previews for exactly this reason.
