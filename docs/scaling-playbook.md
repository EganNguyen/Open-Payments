# Scaling Playbook

How to prove, step by step, that the system reaches 5,000 completed payments/s, and where to record the evidence.

Status: Draft · Last updated: 2026-11-05

Test IDs are defined in [Verification](verification.md). Nothing here has been run yet.

## The process

1. Establish a baseline at low traffic.
2. Increase traffic in steps.
3. Measure where each component becomes the bottleneck.
4. Fix that bottleneck before raising traffic again.
5. Validate under failures, not only normal conditions.
6. Keep spare capacity for spikes and instance loss.

The rule behind all six: **scale on measured bottlenecks, not assumptions.** If the database is already saturated, more API instances make it worse.

## Measures and gates

Measure [admitted req/s, rejected req/s and completed payments/s](architecture.md#goals-and-slos) separately at every step, and never let one stand in for another. Record both platform overhead and end-to-end latency.

**Gates at every step, the same at 1 req/s and at target:**

- Error rate below 0.1%, excluding deliberate rejections in overload tests.
- Platform overhead p99 ≤ 100 ms.
- [CK1-CK6](verification.md#correctness-checks) pass. A queue that grows slowly fails CK6 even if latency looks fine.

A step that misses the final SLO is a finding to fix, not a threshold to relax.

## Ramp plan

Do not move to the next row until the current row meets its gates and you have written down the bottleneck you found.

Steps 0-8 are set by offered load. Steps 9 and 10 are gated on 5,000 completed payments/s. With the [example mix](#workload-and-load-generation), that takes about 8,300 req/s if the 60% share uses auto-capture, so each of those requests completes a payment ([D3](decisions.md#d3)). If authorize and capture are separate requests, only half that share starts a payment, and the target doubles to about 16,700 req/s. Recalculate this from your real mix.

| Step | Load | Duration | Purpose | What to inspect |
|---|---|---|---|---|
| 0 | 1 req/s | 5 min | Baseline and correctness | Single-request latency breakdown, idle resource levels |
| 1 | 10 req/s | 5 min | Correctness under light concurrency | Idempotency, state transitions, ledger balance |
| 2 | 50 req/s | 5 min | First concurrency | ThreadPool queue, lock waits |
| 3 | 100 req/s | 10 min | First capacity test | CPU per instance, pool use, query times |
| 4 | 250 req/s | 10 min | Database validation | Database CPU, I/O, slow queries, WAL rate |
| 5 | 500 req/s | 10 min | Application limit and scale-out | Capacity per instance (C in [Capacity sizing](#capacity-sizing)) |
| 6 | 1,000 req/s | 15 min | Horizontal scaling | Linear gain per added instance, connection budget |
| 7 | 2,000 req/s | 15 min | Data-layer limits | Lock contention, partition and index behavior |
| 8 | 3,000 req/s | 15 min | Asynchronous workloads | Outbox age, consumer lag, webhook backlog |
| 9 | Ramp up to 5,000 completed payments/s | 20 min | Reach target | Whole-system bottleneck, PSP quota |
| 10 | 5,000 completed payments/s sustained (L5) | 60 min | Prove stability | Flat memory, GC, queues, pool wait |
| 11 | Offered load rising to twice step 10 (L6) | 15 min | Find the breaking point | Which resource fails first and how |

Then, once step 10 passes, run L2 (24 h soak), L3 (15,000 req/s overload survival), L4 (abuse traffic), and the failure suite at target load.

**What is active at each point (guide, not a rule):**

| Steps | System shape |
|---|---|
| 0-3 | One API instance, one database, mock PSP. Idempotency, state machine, ledger and outbox table are already in place |
| 4-6 | Add instances behind a load balancer; PgBouncer; connection budget checked |
| 7 | Tune the data layer; add Redis only for non-authoritative uses |
| 8 | Add the broker and consumers; outbox publisher feeds it; isolate webhooks and analytics |
| 9-11 | Full tuning, PSP quota validated, failure injection |

Whether the database needs cells is decided at [gate G1](#decision-gates), not assumed.

## Workload and load generation

Use a mix, not only payment creation. These percentages are example assumptions, to be replaced by your expected production workload.

| Operation | Share |
|---|---|
| Payment authorization and capture | 60% |
| Payment status queries | 15% |
| Refunds | 10% |
| Retries (same idempotency key) | 10% |
| Other operations | 5% |

**Load generator rules:**

- **Use an open model.** Drive a fixed arrival rate, not a fixed number of virtual users. With fixed users, a slow system receives less load and hides its own problem (coordinated omission). In k6, use the `ramping-arrival-rate` executor.
- **Do not let the generator be the bottleneck.** Watch its CPU, use several generators if needed, and run them apart from the system under test.
- **Use unique keys for new payments,** and reuse keys for the retry share.
- **Use a fixed PSP latency** for comparable runs, with separate runs for variable latency and failures.

Sketch, not a finished script. `http_req_failed` counts 429 and 503 as failures, so exclude them from the threshold in overload tests.

```js
import http from 'k6/http';
import { check } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

export const options = {
  scenarios: {
    payments: {
      executor: 'ramping-arrival-rate',
      startRate: 1,
      timeUnit: '1s',
      preAllocatedVUs: 200,
      maxVUs: 20000, // must exceed req/s × request duration
      stages: [
        { target: 10, duration: '5m' },
        { target: 100, duration: '10m' },
        // ... one stage per row of the ramp plan
      ],
    },
  },
  thresholds: {
    http_req_failed: ['rate<0.001'],
    // add duration thresholds once the baseline is recorded
  },
};

export default function () {
  const res = http.post(
    `${__ENV.BASE_URL}/payments`,
    JSON.stringify({ amount_minor: 2400, currency: 'USD', token: 'tok_test' }),
    { headers: { 'Content-Type': 'application/json', 'Idempotency-Key': uuidv4() } }
  );
  check(res, { 'accepted or cleanly rejected': (r) => [200, 201, 202, 429, 503].includes(r.status) });
}
```

After every run, execute CK1-CK6 against the database. A fast run with wrong money is a failed run.

Results from a laptop are good for comparing before and after a change, not as absolute capacity numbers. Run the final steps on Linux machines, with load generators elsewhere.

## Capacity sizing

**Instances needed:**

```
N = ceil( T / (C × U) )
```

- `T`: target req/s, derived from the completed-payment goal and the mix.
- `C`: capacity of one instance, **measured** in step 5 while meeting the gates.
- `U`: maximum utilization you allow, for example 0.7.

Illustration only: if one instance proves 500 req/s, at 70% utilization and `T` of 8,300, `N = ceil(8300 / (500 × 0.7)) = 24`. Add at least one more (N+1) if the target must hold after an instance fails.

More instances do not raise database capacity. Benchmark the database alone (step 4), then the whole system.

**Concurrency (Little's Law):** `L = λ × W`, so concurrent requests equal throughput times average time in the system.

| Average duration at 8,300 req/s | In-flight requests |
|---|---|
| 20 ms | 166 |
| 50 ms | 415 |
| 100 ms | 830 |
| 200 ms | 1,660 |
| 500 ms | 4,150 |
| 1 s | 8,300 |

Divide by the instance count to set the [per-instance in-flight cap](engineering-standards.md#load-control), and check that the cap and timeouts hold. Never solve this with unbounded tasks.

**Autoscaling and the database:** more pods mean more connections. Keep `max replicas × connections per pod` inside the [connection budget](engineering-standards.md#database).

## Bottleneck runbook

| Observation | Likely bottleneck | First action | Check |
|---|---|---|---|
| API CPU near 95% | Application compute | Profile CPU, optimize hot path | CPU profile, allocation rate |
| ThreadPool queue grows | Blocking or thread starvation | Find synchronous blocking | `dotnet-counters`, S1 analyzer output |
| Database connections exhausted | Connection pool | Check query duration and connection hold time | Pool wait time, PgBouncer stats |
| Database CPU near 95% | Database capacity | Read plans of the top queries | Slow query log, `EXPLAIN` |
| Lock waits rise | Contention | Shorten transactions, find hot rows, read the [deadlock log](deadlock-handling.md#observability) | Lock wait metrics, `pg_stat_database.deadlocks` |
| Database I/O or WAL saturated | Storage or commit rate | Check commit latency, WAL volume, batching | Disk and WAL metrics |
| Kafka consumer lag grows | Consumer capacity | Profile consumers; add workers or partitions | Lag per group, handler time |
| Outbox age grows | Publisher or broker slow | Check batch size and broker health | Outbox age metric |
| PSP latency or errors rise | External dependency | Check provider limits and breaker state | Adapter metrics |
| p99 rises while CPU is low | Waiting on a dependency or queueing | Trace individual slow requests | Distributed traces |
| Memory grows steadily | Allocation or leak | Inspect GC counters, take a heap dump | Gen 2 rate, heap size |
| Gen 2 GC or pause rate rises | Allocation pressure | Reduce hot-path allocations | Allocation rate, S2 benchmarks |

## Decision gates

A gate is a measured question with a recorded answer. Do not go past an unresolved gate.

| Gate | Question | If the answer is no |
|---|---|---|
| G1: Database | Can one primary sustain the measured write rate at 60% CPU or less with synchronous replication on? | Split into [cells](architecture.md#cells-and-routing) and repeat steps 4-9 per cell |
| G2: PSP | Do the PSP contract and real sandbox allow the target rate and latency? | Add PSPs or renegotiate before more engineering |
| G3: Commit latency | Does synchronous replication keep overhead p99 within 100 ms? | Tune commit batching or add cells. Moving to asynchronous replication gives up zero data loss and is an owner decision |
| G4: Ledger check | Is the deferred balance trigger affordable at full load? | Replace it with the continuous audit query plus [property tests](architecture.md#ledger) |
| G5: Headroom | Does the system meet the SLO with one instance or one zone lost? | Add capacity or fix failover before sign-off |

## Results log

Copy this block for every run, and keep the files in the repository next to the run's raw data.

```
Run ID:
Date / commit / configuration file:
Step and load profile:
Duration:
Instances (API / DB / broker) and sizes:

Achieved: admitted req/s ____ rejected req/s ____ completed payments/s ____
Platform overhead latency: p50 ____ p95 ____ p99 ____
End-to-end latency (mock PSP fixed at ____ ms): p50 ____ p95 ____ p99 ____
Error rate (excluding deliberate rejections): ____

Resources: API CPU ____ memory ____ | DB CPU ____ I/O ____ | broker ____
Runtime: ThreadPool queue ____ Gen 2 GC/s ____ allocation rate ____
Database: pool wait ____ lock waits ____ deadlocks ____ WAL rate ____
Async: outbox age ____ consumer lag ____

Correctness: CK1 ☐ CK2 ☐ CK3 ☐ CK4 ☐ CK5 ☐ CK6 ☐
Gates passed: ☐ yes ☐ no

Bottleneck found:
Action taken:
Evidence link (dashboard snapshot, trace, profile):
Next step:
```

## Final report

| Deliverable | Acceptance criteria |
|---|---|
| Baseline benchmark | Reproducible 1 req/s results |
| Payment correctness tests | Idempotency, ledger and state transitions verified (U1-U4, C5, C6) |
| API capacity benchmark | Measured C per instance |
| Database benchmark | Measured write and read capacity; G1 answered |
| Horizontal scaling | Capacity grows with instances; connection budget respected |
| Broker benchmark | Consumers keep up; CK6 passes |
| PSP simulator | Configurable latency, failures and throughput limit |
| Load test automation | Repeatable full ramp from one command |
| Observability dashboards | All [metrics](engineering-standards.md#observability) visible |
| Failure injection | C1-C42 pass (C33 only if cells are adopted); no lost or duplicate successful payments |
| Final capacity report | 5,000 completed payments/s sustained for 60 min with gates met |

**Report outline:**

1. Target, workload mix and environment.
2. Results table: one row per step, linking to run logs.
3. Measured capacity per instance and instances needed (N, N+1).
4. Database capacity, and answers to G1-G5.
5. Bottlenecks found, in order, with the fix for each.
6. Failure test results and correctness checks.
7. Maximum capacity found in the stress step and what failed first.
8. Remaining risks and recommended headroom.

The most important result is not reaching the target. It is knowing why the system can handle it, what its limit is, and how it behaves when something fails.
