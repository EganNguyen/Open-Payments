# Scaling Playbook: from 1 to 5,000 requests per second

Companion to `payment-system-design.md` and `failure-scenarios.md` (what can fail and how to recover). The design says what the system is and why it is correct. This playbook says how to prove it scales, step by step, and where to record the evidence. Test IDs (L1, C3, CK2 and so on) refer to section 18 of the design.

Status: draft v1 · Date: 2026-11-04

## 1. The process

1. Establish a baseline at low traffic.
2. Increase traffic in steps.
3. Measure where each component becomes the bottleneck.
4. Fix that bottleneck before raising traffic again.
5. Validate under failures, not only normal conditions.
6. Keep spare capacity for spikes and instance loss.

The rule behind all six: **scale on measured bottlenecks, not assumptions.** Do not add servers until a measurement says the application tier is the limit. If the database is already saturated, more API instances make it worse.

## 2. Definitions

**Measure these separately at every step:**

| Metric | Meaning |
|---|---|
| Admitted requests/s | HTTP requests that passed admission control |
| Rejected requests/s | Requests shed with 429 or 503 (expected only in overload tests) |
| Completed payments/s | Payments that reached a final committed state |

5,000 HTTP requests/s is not 5,000 completed payments/s. The target in the design is completed payments, so report both and never let one stand in for the other.

**Two latency definitions, used together:**

- **Platform overhead:** request time excluding time spent waiting on the PSP. Gated target from the design: p99 ≤ 100 ms, p50 ≤ 25 ms.
- **End to end:** full request time with the mock PSP at a fixed latency. Recorded at every step. It becomes a gate only after you choose a target against the real PSP.

**Gates at every step (the same at 1 req/s and at 5,000):**

- Error rate below 0.1%, excluding deliberate rejections in overload tests.
- Platform overhead p99 ≤ 100 ms.
- CK1-CK6 pass (section 4.1 of this document lists CK6; CK1-CK5 are in design §18.1).

The same targets apply at every step. Looser thresholds at intermediate load can hide regressions, so a step that misses the final SLO is a finding to fix, not a threshold to relax.

## 3. Ramp plan

Do not move to the next row until the current row meets its gates and you have written down the bottleneck you found.

| Step | Load | Duration | Purpose | What to inspect |
|---|---|---|---|---|
| 0 | 1 req/s | 5 min | Baseline and correctness | Single-request latency breakdown, resource idle levels |
| 1 | 10 req/s | 5 min | Correctness under light concurrency | Idempotency, state transitions, ledger balance |
| 2 | 50 req/s | 5 min | First concurrency | ThreadPool queue, lock waits |
| 3 | 100 req/s | 10 min | First capacity test | CPU per instance, DB pool use, query times |
| 4 | 250 req/s | 10 min | Database validation | DB CPU, I/O, slow queries, WAL rate |
| 5 | 500 req/s | 10 min | Application limit and scale-out | Capacity per instance (C in section 5) |
| 6 | 1,000 req/s | 15 min | Horizontal scaling | Linear gain per added instance, connection budget |
| 7 | 2,000 req/s | 15 min | Data-layer limits | Lock contention, partition and index behavior |
| 8 | 3,000 req/s | 15 min | Asynchronous workloads | Outbox age, consumer lag, webhook backlog |
| 9 | 5,000 req/s (ramp up) | 20 min | Reach target | Whole-system bottleneck, PSP quota |
| 10 | 5,000 req/s sustained | 60 min | Prove stability | Flat memory, GC, queues, pool wait |
| 11 | 5,000 to 10,000 req/s | 15 min | Find the breaking point | Which resource fails first and how |

Then, once step 10 passes:

- **L2:** 24 h soak at 5,000 for leaks and slow drift.
- **L3:** 15,000 burst for 10 min for load shedding and recovery.
- **L4:** abuse traffic (slow bodies, oversized bodies, connection floods).
- **C1-C33:** the failure suite, run at 5,000 req/s (design §18.3).

**What is active at each point (guide, not a rule):**

| Steps | System shape |
|---|---|
| 0-3 | One API instance, one database, mock PSP. Idempotency, state machine, ledger, outbox table already in place (the outbox is a correctness mechanism, so it exists from the start) |
| 4-6 | Add instances behind a load balancer; PgBouncer; connection budget checked |
| 7 | Tune the data layer; add Redis only for non-authoritative uses |
| 8 | Add the broker and consumers; outbox publisher feeds it; isolate webhooks and analytics |
| 9-11 | Full tuning, PSP quota validated, failure injection |

Whether the database needs cells (design §5) is decided at the gate in section 7, not assumed.

## 4. Workload mix and load generation

### 4.1 Extra invariant

**CK6:** no sustained growth in queue depth, outbox age or consumer lag during any step. A queue that grows slowly is a failed step even if latency looks fine.

### 4.2 Workload mix

Use a mix, not only payment creation. These percentages are example assumptions. Replace them with your expected production workload.

| Operation | Share |
|---|---|
| Payment authorization and capture | 60% |
| Payment status queries | 15% |
| Refunds | 10% |
| Retries (same idempotency key) | 10% |
| Other operations | 5% |

### 4.3 Load generator rules

- **Use an open model.** Drive a fixed arrival rate, not a fixed number of virtual users. With a fixed number of users, a slow system automatically receives less load and hides its own problem (this is called coordinated omission). In k6 use the `ramping-arrival-rate` executor.
- **Do not let the generator be the bottleneck.** Watch its CPU and use several generators if needed. Run them on different machines from the system under test.
- **Use unique keys for new payments** and reuse keys for the retry share of the mix.
- **Use a fixed PSP latency** in the mock for comparable runs, and separate runs with variable latency and failures.

Sketch (not a finished script):

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
      maxVUs: 5000,
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

After every run, execute the correctness checks CK1-CK6 against the database. A fast run with wrong money is a failed run.

### 4.4 Running on a laptop

Results from a laptop (containers in a VM, shared CPU, thermal limits) are good for comparing before and after a change. They are not absolute capacity numbers. Run the final steps on Linux machines, with load generators elsewhere.

## 5. Capacity sizing

### 5.1 Instances needed

```
N = ceil( T / (C × U) )
```

- `T`: target throughput
- `C`: capacity of one instance, **measured** in step 5 while meeting the gates
- `U`: maximum utilization you allow (for example 0.7)

Illustration only: if one instance proves 500 req/s and you allow 70% utilization, `N = ceil(5000 / (500 × 0.7)) = 15`. The 500 is an example. Use your measured C. Add one more instance (N+1), or more, if 5,000 must hold after an instance fails.

More instances do not raise database capacity. Benchmark the database alone (step 4), then the whole system.

### 5.2 Concurrency (Little's Law)

`L = λ × W` (concurrent requests = throughput × average time in system).

| Average request duration at 5,000 req/s | In-flight requests |
|---|---|
| 20 ms | 100 |
| 50 ms | 250 |
| 100 ms | 500 |
| 200 ms | 1,000 |
| 500 ms | 2,500 |
| 1 s | 5,000 |

If the PSP takes 1 s, up to 5,000 requests wait on it at once. Divide by the instance count to set the per-instance in-flight cap (design §12.1), and check that the cap and timeouts hold. Never solve this with unbounded tasks.

### 5.3 Autoscaling and the database

More pods mean more database connections. Set the autoscaler's maximum replicas so that `max replicas × connections per pod` stays inside the connection budget (design §14.1). Otherwise scaling out can overload the database.

## 6. When a step fails: bottleneck runbook

| Observation | Likely bottleneck | First action | Check |
|---|---|---|---|
| API CPU near 95% | Application compute | Profile CPU, optimize hot path | CPU profile, allocation rate |
| ThreadPool queue grows | Blocking or thread starvation | Find synchronous blocking | `dotnet-counters`, S1 analyzer output |
| Database connections exhausted | Connection pool | Check query duration and connection hold time | Pool wait time, PgBouncer stats |
| Database CPU near 95% | Database capacity | Read execution plans of the top queries | Slow query log, `EXPLAIN` |
| Lock waits rise | Contention | Shorten transactions, find hot rows | Lock wait metrics |
| Database I/O or WAL saturated | Storage or commit rate | Check commit latency, WAL volume, batching | Disk and WAL metrics |
| Kafka consumer lag grows | Consumer capacity | Profile consumers; add workers or partitions | Lag per group, handler time |
| Outbox age grows | Publisher or broker slow | Check batch size and broker health | Outbox age metric |
| PSP latency or errors rise | External dependency | Check provider limits and breaker state | Adapter metrics |
| p99 rises while CPU is low | Waiting on a dependency or queueing | Trace individual slow requests | Distributed traces |
| Memory grows steadily | Allocation or leak | Inspect GC counters, take a heap dump | Gen 2 rate, heap size |
| Gen 2 GC or pause rate rises | Allocation pressure | Reduce hot-path allocations | Allocation rate, S2 benchmarks |

## 7. Decision gates

A gate is a measured question with a recorded answer. Do not skip ahead past an unresolved gate.

| Gate | Question | If the answer is no |
|---|---|---|
| G1: Database | Can one primary sustain the measured write rate at 60% CPU or less with synchronous replication on? | Split into cells (design §5) and repeat steps 4-9 per cell |
| G2: PSP | Does the PSP contract and the real sandbox allow the target TPS and latency? | Add PSPs or renegotiate before more engineering |
| G3: Commit latency | Does synchronous replication keep overhead p99 within 100 ms? | Tune commit batching or add cells. Dropping to asynchronous replication gives up zero data loss and is an owner decision |
| G4: Ledger check | Is the deferred balance trigger affordable at full load? | Replace with the continuous audit query plus property tests (design §9) |
| G5: Headroom | Does the system meet the SLO with one instance or one zone lost? | Add capacity or fix the failover behavior before sign-off |

## 8. Results log

Copy this block for every run and keep the files in the repository next to the run's raw data.

```
Run ID:
Date / commit / configuration file:
Step (from section 3) and load profile:
Duration:
Instances (API / DB / broker) and sizes:

Achieved: admitted req/s ____ rejected req/s ____ completed payments/s ____
Platform overhead latency: p50 ____ p95 ____ p99 ____
End-to-end latency (mock PSP fixed at ____ ms): p50 ____ p95 ____ p99 ____
Error rate (excluding deliberate rejections): ____

Resources: API CPU ____ memory ____ | DB CPU ____ I/O ____ | broker ____
Runtime: ThreadPool queue ____ Gen 2 GC/s ____ allocation rate ____
Database: pool wait ____ lock waits ____ WAL rate ____
Async: outbox age ____ consumer lag ____

Correctness: CK1 ☐ CK2 ☐ CK3 ☐ CK4 ☐ CK5 ☐ CK6 ☐
Gates passed: ☐ yes ☐ no

Bottleneck found:
Action taken:
Evidence link (dashboard snapshot, trace, profile):
Next step:
```

## 9. Deliverables and final report

| Deliverable | Acceptance criteria |
|---|---|
| Baseline benchmark | Reproducible 1 req/s results |
| Payment correctness tests | Idempotency, ledger and state transitions verified (U1-U4, C5, C6) |
| API capacity benchmark | Measured C per instance |
| Database benchmark | Measured write and read capacity; gate G1 answered |
| Horizontal scaling | Capacity grows with added instances; connection budget respected |
| Broker benchmark | Consumers keep up; CK6 passes |
| PSP simulator | Configurable latency, failures and throughput limit |
| Load test automation | Repeatable 1 to 5,000 req/s run from one command |
| Observability dashboards | All metrics in design §17 visible |
| Failure injection | C1-C33 pass; no lost or duplicate successful payments |
| Final capacity report | 5,000 req/s sustained for 60 min with gates met |

**Final capacity report outline:**

1. Target, workload mix and environment.
2. Results table: one row per step, linking to run logs.
3. Measured capacity per instance and instances needed (N, N+1).
4. Database capacity, gates G1-G5 and their answers.
5. Bottlenecks found, in order, with the fix for each.
6. Failure test results (C1-C33) and correctness checks.
7. Maximum capacity found in the stress step and what failed first.
8. Remaining risks and recommended headroom.

The most important result is not reaching 5,000. It is knowing exactly why the system can handle it, what its limit is, and how it behaves when something fails.
