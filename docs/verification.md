# Verification

The only home for test IDs, the production checklists, progress counts and the build phases that tie them together.

Status: Draft · Last updated: 2026-11-09

## How verification works

A design on paper cannot pass production problems. So this doc does three things:

1. It lists each production problem and the mechanism meant to answer it.
2. It names the test or drill that proves the mechanism works.
3. It leaves every checkbox unticked until that test passes with evidence.

A test passes only when its run is recorded with a run ID, commit, configuration and result link. A checklist row is ticked only when all its proofs have passed. Until then the status is "designed, not verified".

The [Production problem checklist](#production-problem-checklist) is the canonical list of 108 problems.

## Correctness checks

Run after every load and chaos test.

| ID | Check |
|---|---|
| CK1 | Ledger is balanced for every transaction and no ledger row was changed or deleted |
| CK2 | Every payment has at most one successful authorize, one successful capture, and one effect per refund at the PSP (compared by the PSP key of each operation) |
| CK3 | `refunded_minor + refund_pending_minor ≤ captured_minor` for every payment, and `refund_pending_minor` equals the sum of its unresolved refunds |
| CK4 | Every outbox row is eventually published, and every event had exactly one effect in each consumer |
| CK5 | No payment, capture attempt or refund remains unresolved past the reconciler threshold, and no PSP event stays parked past its limit |
| CK6 | No sustained growth in queue depth, outbox age or consumer lag |
| CK7 | Clearing: no clearing account balance is nonzero past its allowed age |
| CK8 | Timeliness: settlement files and other inputs arrived within their window |
| CK9 | Completeness: every source record has its ledger match, both ways, such as a `CAPTURED` payment with its capture transaction |
| CK10 | Cross-check: independent records agree, such as `payments.captured_minor` and the ledger sum for that payment |
| CK11 | Client view: every acknowledged answer has its payment with the same ID and a state that answer allows; every unanswered key is classified into a PSP lifecycle case L0-L5; every replay returned the original ID; every unresolved attempt is listed ([D52](decisions.md#d52)). Runs where the fault driver ran |

CK7-CK10 are separate code from the payment-transition module ([D23](decisions.md#d23)). Every check must fail on injected corruption and on zero rows.

**Which checks apply** ([D24](decisions.md#d24)). A check that can't apply yet is reported as not applicable, never as passed. Runs before Phase 3 are previews and tick no checklist row.

| Stage | CK1-CK3 | CK4 | CK5 | CK6 | CK7-CK10 |
|---|---|---|---|---|---|
| Database spike | CK1 and CK3 by SQL | n/a | n/a | n/a | n/a |
| After Phase 1 | Yes | Every transition has its outbox row | Yes, with no PSP timeouts injected | Without outbox age | CK9, CK10 |
| After Phase 2 | Yes | Same as Phase 1 | Yes, with timeouts | Without outbox age | Yes |
| Phase 3 onward | Yes | Full | Yes | Full | Yes |

## Load tests

| ID | Test | Pass criteria |
|---|---|---|
| L1 | Ramp per the [playbook](scaling-playbook.md#ramp-plan), one step at a time up to 5,000 completed payments/s, mixed workload, against the mock PSP with realistic latency and 2-5% errors | Gates met at every step: p99 platform overhead ≤ 100 ms; error rate (excluding deliberate rejections) < 0.1%; CK1-CK6 pass; results logged for the capacity model |
| L2 | Soak: 5,000 completed payments/s for 24 h. The rate is under owner review ([D36](decisions.md#d36)) | Memory and Gen 2 GC rate show no upward trend; ThreadPool queue stable; no pool wait growth; CK1-CK6 pass |
| L3 | Overload survival: 15,000 req/s for 10 min | Excess rejected with 429 or 503; 5,000 completed payments/s holds; no OOM or restart; SLO restored within 2 min after the burst; CK1-CK6 pass |
| L4 | Abuse: slow-body clients, oversized bodies, connection floods | Abusive clients are cut off; normal traffic keeps its SLO |
| L5 | Sustained: 5,000 completed payments/s for 60 min | Gates met throughout; memory, queues and pool wait stay flat |
| L6 | Stress: offered load rising to twice the L5 load for 15 min | Overload is shed with 429 or 503; the first resource to fail is identified and recorded |

## Chaos and failure tests

| ID | Fault injected | Expected behavior |
|---|---|---|
| C1 | Database 5x slower | Backpressure and fast rejection; no ThreadPool starvation; no retry storm; CK pass |
| C2 | Database connection pool exhausted | Bounded wait, then controlled failure; recovery without restart |
| C3 | PSP approves an authorize, capture or refund, then the response times out | Authorize makes the payment `UNKNOWN`; capture and refund stay unresolved on their operation row, with no state change. Reconciler resolves each; exactly one effect at the PSP |
| C4 | API process killed (`kill -9`) after the PSP approved | Reconciler recovers the real state; exactly one charge |
| C5 | Same idempotency key sent 100 times concurrently | One payment, one charge, identical stored response to all |
| C6 | 50 concurrent refunds totaling more than the captured amount | Refunded plus pending never exceeds captured; the rest rejected; declined refunds release their reservation |
| C7 | Database primary killed during load | Failover within RTO; all committed payments present (RPO 0); fresh reads served by the new primary |
| C8 | Broker down 10 min, and one broker killed | Outbox retains events; backlog drains after recovery; CK4 passes |
| C9 | Duplicate, delayed and out-of-order PSP callbacks | One logical state transition each; stale events ignored and logged |
| C10 | Kubernetes node killed | Remaining replicas serve traffic within SLO |
| C11 | Availability zone lost | Cell keeps serving from the other zones; no data loss |
| C12 | PSP returns 429 | Client-side limiter and backoff hold; no duplicate charges |
| C13 | PSP outage for 5 min | Breaker opens, failover to secondary PSP where allowed, fast failures otherwise |
| C14 | Fraud service unavailable | Fallback policy applied per tier; decisions logged |
| C15 | Failure injected into transaction B (state, ledger and outbox write) | Payment not confirmed to the client; remains pending; reconciler completes it; no partial ledger |
| C16 | PSP DNS failure and TLS certificate expiry | Alerts fire; connections recover after DNS or certificate is fixed |
| C17 | Consumer killed mid-batch, plus a rebalance | No lost events and no double effects (inbox dedupe) |
| C18 | Poison message in a topic | Retried, then dead-lettered with alert; the partition keeps moving |
| C19 | Deliberately bad release | Canary stops promotion and auto-rolls back on SLO breach |
| C20 | SIGTERM during load | In-flight requests complete; no lost work; outbox drained |
| C21 | Secret or credential expiry | Expiry alert fired in advance; rotation succeeds without downtime |
| C22 | Disk fill on database, broker and log volumes | Alerts fire at 70% and 85%; documented action prevents outage |
| C23 | PSP latency raised to 2 s at 5k TPS | In-flight work stays bounded; limiter sheds excess; slow calls become `UNKNOWN` and are reconciled; no memory growth, no duplicate charge |
| C24 | Cache or Redis unavailable | Rate limits fall back to in-process limits; idempotency still enforced by the database; CK pass |
| C25 | Clock skew of 5 min on one node | Signatures and token checks use tolerance; ordering uses database time; no wrong state transitions |
| C26 | Reconciler stopped for 30 min, then two reconcilers run at once | Backlog drains with a rate cap; only one worker acts on a batch; no double ledger entry |
| C27 | Merchant webhook endpoint slow or down for 24 h | Backoff with jitter; that merchant's queue is isolated; other merchants' delivery is unaffected; resend works afterwards |
| C28 | Full outage, then restart of all services | Staged ramp and capped backlog drain; no second outage; CK pass before recovery is declared |
| C29 | Replica lag of 30 s | Reads that must be fresh go to the primary; no stale payment status after create |
| C30 | One merchant sends 50% of all traffic | Per-merchant limits protect others; other merchants keep their SLO |
| C31 | Edge-case suite for payment handling ([E1-E29](failure-scenarios.md#edge-cases)), split by phase ([D46](decisions.md#d46)): C31a (E1-E3, E5-E8, E11-E13, E16-E18, E21, E25-E29), C31b (E4, E9, E10, E19, E20, E22-E24), C31c (E14, E15) | Each case returns its defined result; no state or ledger corruption |
| C32 | PSP returns an amount or currency that differs from ours | Payment moves to `REVIEW`; no ledger entry for the disputed amount; alert fires |
| C33 | Cell router or its config store unavailable (only if cells are adopted) | Routers keep the last mapping; mapping changes frozen; traffic keeps flowing |
| C34 | Capture, refund and reconciler race on one payment, with lock order forced to collide | `40P01` is raised and counted in `pg_stat_database.deadlocks`; retried at most twice; `55P03` returns 503; exactly one effect per operation; CK1 and CK3 pass |
| C35 | Deferred with [D38](decisions.md#d38), and required before fees or payouts ship. Concurrent refunds across different payments against a low merchant balance | Advisory lock serializes the checks; balance never goes negative; create-payment latency unaffected |
| C36 | Two concurrent capture requests with different idempotency keys | One PSP capture; the other gets the current status or 409; CK2 passes |
| C37 | Decline, then two concurrent new attempts on the same payment | One PSP authorize; the other gets the current status or 409; one payment for the order; CK2 passes |
| C38 | PSP approves an attempt already marked `FAILED`, both before and after another attempt succeeds | Attempt moves to `REVIEW`, alert fires, no ledger entry for it; no new attempt can start from `AWAITING_METHOD`. Where the PSP allows, the reconciler voids it, and that void doesn't block the payment's own capture ([D37](decisions.md#d37)) |
| C39 | One real 3D Secure flow on a PSP sandbox: finished, failed and abandoned, plus a callback racing the action window | Correct state each time; abandoned reaches `AWAITING_METHOD` through `UNKNOWN` and inquiry; one effect at the PSP. Required before the API contract is frozen ([D17](decisions.md#d17)) |
| C40 | Settlement file with duplicate lines, unmatched lines, wrong amounts and a missing line, ingested twice | Each line ends in the defined state; discrepancies page; ledger entries post once; a killed ingest resumes safely |
| C41 | Key expiry job runs while payments are `UNKNOWN` past 24 hours, then the client retries | Key is kept, the stored answer returns, and there is no second charge; resolved keys expire |
| C42 | Test-mode keys used against live data and live keys against test data, with test traffic flooding under load | Isolation holds in both directions; test traffic is shed first; live keeps its SLO ([D20](decisions.md#d20)) |
| C43 | Stall both standbys and cancel the request while `COMMIT` waits. Hold the stall past the commit wait limit. Separately, drop the connection during `COMMIT` for transactions A and B | No cancel request reaches the server; no client is told a commit succeeded unless a standby has it, in a run with the fault verified. The runner stops waiting at its limit, and an abandoned commit releases its connection once a standby returns. After A, no PSP call is made. After B, the answer is 202 and the attempt stays findable; CK2 passes ([D28](decisions.md#d28)) |
| C44 | PSP lookup says not found, then the delayed request lands and is approved | The attempt isn't failed by the lookup alone. No second attempt can charge. With a proof-giving mode, a late approval still goes to `REVIEW`; CK2 passes ([D27](decisions.md#d27)) |
| C45 | Attempt unresolved past the PSP's key retention, under each recovery mode | No replay is sent. The attempt stays `UNKNOWN` and escalates; no second charge |
| C46 | Kill the process between callback receipt and commit; deliver a callback before transaction B commits; redeliver both | Each event applies once. The early event parks, then applies. None is dropped as already seen ([D30](decisions.md#d30)) |
| C47 | PSP slows until thousands of attempts are `UNKNOWN`, then recovers | Recovery stays inside its own quota; live payments keep theirs; backlog drains with a rate cap; CK2 and CK5 pass ([D34](decisions.md#d34)) |
| C48 | Callback flood at several times the normal rate | Callbacks are limited with their own permits and the PSP's retries deliver the rest; create-payment keeps its SLO; no event is lost |
| C49 | Status-query flood | Create-payment keeps its SLO; reads shed first ([Stripe research](stripe-research.md#8-separate-rate-limits-from-load-shedding)) |
| C50 | Capture and void at the same moment; void times out; authorization window passes during a live capture; capture after `VOIDED` or `EXPIRED` | One PSP effect; the loser gets the current status or 409; a timeout stays unresolved with no state change; expiry waits for the live capture; `auth_hold` clears in every final state ([D37](decisions.md#d37)) |
| C51 | One merchant's PSP credential is revoked during load | That merchant's attempts fail fast with a stable code and no `UNKNOWN`; its account breaker opens and the PSP's doesn't; other merchants keep their SLO; an alert fires ([D35](decisions.md#d35)) |
| C52 | Transaction B commits, then the HTTP response is lost; the client retries the key | Every retry returns the original payment ID and outcome; one PSP effect; CK2 and CK11 pass ([D51](decisions.md#d51)) |
| C53 | 200 ms latency, then resets, between the API and PgBouncer at step 4 | Queues stay bounded; excess shed as 429 or 503; no duplicate; first success ≤ 30 s and SLO ≤ 2 min after removal; CK2 and CK11 pass |
| C54 | PostgreSQL primary restarted under load at step 4 (local form of C7) | Every acknowledged payment present afterwards; outage answers are 503, not 500; recovery inside the C53 limits; CK11 passes |
| C55 | Client resends a key while the original request is still waiting on the PSP | The retry renders current state ([D31](decisions.md#d31)); one attempt; one PSP effect; CK2 and CK11 pass |
| TX1 | 100 pairs of opposite-order updates on a two-row test-only schema, through the real `TxRunner` | `pg_stat_database.deadlocks` rises; all 200 transactions commit; no lost update; at most 2 retries each. Proves deadlock recovery, not product concurrency |
| TX2 | Transaction work raises SQLSTATE 40P01 on every attempt | Exactly 3 attempts, then `retry_exhausted` and 503; nothing committed; the exhaustion is counted |

## Unit and property tests

| ID | Test |
|---|---|
| U1 | Money arithmetic and currency rounding property tests (no floating point; exponent per currency) |
| U2 | State machine: every legal transition, every illegal and out-of-order event |
| U3 | Ledger property tests: always balanced, append-only, reservations never produce a negative balance |
| U4 | Idempotency unit and integration tests (same key, same hash; same key, different hash; in-flight) |
| U5 | Contract tests: event schemas (backward compatibility, size limit), PSP adapter recorded fixtures, each adapter's declared capabilities, and amount conversion per currency |
| U6 | Tenant isolation: a key for merchant A can never read or change merchant B's data |
| U7 | Webhook and request signature, timestamp window and replay tests |
| U8 | Log redaction: scanning logs for card-number patterns finds none |
| F1 | Card-testing simulation: velocity rules block the pattern and alert |

**Lifecycle scenarios that U2, U3 and U4 must cover:**

- For every state, try every transition; only the allowed ones succeed.
- The sum of ledger entries per payment equals the expected balance for its final state.
- Replay every request twice, and with a crash in between.
- Run capture and refund at the same moment; exactly one wins and the ledger balances.
- Deliver PSP callbacks in all permutations; the final state is the same.
- Decline, then retry on the same payment, up to the attempt cap; at most one attempt ever succeeds.
- Force PSP timeouts; the payment reaches a correct final state through the reconciler.

## Static gates

Run in CI or at release.

| ID | Gate |
|---|---|
| S1 | Analyzer fails the build on `.Result`, `.Wait()`, `.GetAwaiter().GetResult()`, sync I/O, `new Thread` and `Task.Run` in request-path projects |
| S2 | Allocation and performance gate: BenchmarkDotNet budgets for hot-path requests; review of closures, LINQ, string and buffer use |
| S3 | SQL gate: `EXPLAIN` on hot queries, query-count tests for N+1, review of generated SQL, tracking mode review |
| S4 | Migration review: expand/contract, `lock_timeout`, `CONCURRENTLY` indexes |
| S5 | Code analyzers and review: `IDisposable`, `CancellationToken` flow, async locking rule, bounded concurrency, scoped `DbContext` |
| S6 | Dependency, license and secret scanning |

## Operational drills

| ID | Drill |
|---|---|
| O1 | Dashboards and alerts from [Observability](engineering-standards.md#observability) exist and fire in drills |
| O2 | Profiling runbooks (`dotnet-counters`, `dotnet-trace`, `dotnet-dump`) and heap-dump analysis exercised |
| O3 | Capacity model (pool sizes, partitions, headroom, cell count) reviewed against L1-L3 results |
| O4 | Security and access review: RBAC, audit log, key scopes, PCI scope, MFA and step-up |
| O5 | Backup restore and disaster-recovery drill |
| O6 | Secret or key leak response drill ([runbook R6](failure-scenarios.md#runbooks)) |

## Production problem checklist

The **Design response** column links to the mechanism, and **Proof** lists tests from this doc. Three problem names repeat because they apply at more than one layer: rows 3 and 26, 29 and 43, 30 and 44.

### ASP.NET Core and HTTP server

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 1 | Sudden traffic spike | [Load control](engineering-standards.md#load-control): per-merchant rate limit, global limiter, fast rejection | L3 | ☐ |
| 2 | Too many concurrent requests | [Load control](engineering-standards.md#load-control): concurrency limiter, in-flight cap, no queue | L3 | ☐ |
| 3 | ThreadPool starvation | [.NET runtime](engineering-standards.md#net-runtime): async end to end; ThreadPool queue alert ([Observability](engineering-standards.md#observability)) | S1, L2, O1 | ☐ |
| 4 | Blocking synchronous I/O | [.NET runtime](engineering-standards.md#net-runtime): analyzer bans sync I/O in request path | S1 | ☐ |
| 5 | Long-running HTTP requests | [Load control](engineering-standards.md#load-control): total request budget and deadlines | C3, C1 | ☐ |
| 6 | Large request payload | [Load control](engineering-standards.md#load-control): 64 KB body limit | L4 | ☐ |
| 7 | Slow clients | [Load control](engineering-standards.md#load-control): header timeout and minimum data rate | L4 | ☐ |
| 8 | Connection exhaustion | [Load control](engineering-standards.md#load-control): connection limit; load balancer timeouts above keep-alive | L3, L4 | ☐ |
| 9 | HTTP connection reset | [Payment flow](architecture.md#payment-flow): state committed before PSP call; safe client retry via idempotency | C4, C5 | ☐ |
| 10 | Application instance crashes | [Kubernetes](engineering-standards.md#kubernetes-and-operations): at least 3 replicas across 3 zones, PodDisruptionBudgets | C10 | ☐ |

### .NET runtime, GC and memory

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 11 | Excessive allocations | [.NET runtime](engineering-standards.md#net-runtime): allocation budget enforced in CI | S2 | ☐ |
| 12 | Large Object Heap pressure | [.NET runtime](engineering-standards.md#net-runtime): payload caps, `ArrayPool` where measured | S2, L2 | ☐ |
| 13 | Gen 2 GC frequency | [.NET runtime](engineering-standards.md#net-runtime): Server GC; Gen 2 rate alert ([Observability](engineering-standards.md#observability)) | L2, O1 | ☐ |
| 14 | Large GC pauses | [.NET runtime](engineering-standards.md#net-runtime): concurrent Server GC, heap hard limit, p99 alert | L2, L1 | ☐ |
| 15 | Memory leak | [.NET runtime](engineering-standards.md#net-runtime): bounded caches; soak memory trend; heap-dump runbook | L2, O2 | ☐ |
| 16 | Unbounded task creation | [Load control](engineering-standards.md#load-control): bounded `Channel<T>` and fixed workers | S5, L3 | ☐ |
| 17 | Excessive closures | [.NET runtime](engineering-standards.md#net-runtime): no closures in hot loops, profile-driven | S2 | ☐ |
| 18 | Excessive LINQ allocations | [.NET runtime](engineering-standards.md#net-runtime): no LINQ in hot loops; Dapper on write path | S2 | ☐ |
| 19 | Excessive string allocations | [.NET runtime](engineering-standards.md#net-runtime): source-generated JSON and logging, spans | S2 | ☐ |
| 20 | Array copying | [.NET runtime](engineering-standards.md#net-runtime): `ArrayPool` and `Span<T>` where measured | S2 | ☐ |
| 21 | Large object retention | [.NET runtime](engineering-standards.md#net-runtime): scoped lifetimes; `MemoryCache` with size limit | L2 | ☐ |
| 22 | Incorrect `IDisposable` usage | [.NET runtime](engineering-standards.md#net-runtime): analyzers, `using` and `await using` | S5 | ☐ |
| 23 | Excessive boxing | [.NET runtime](engineering-standards.md#net-runtime): `Money` as struct, generics | S2 | ☐ |
| 24 | Pinned objects | [.NET runtime](engineering-standards.md#net-runtime): no pinning unless unavoidable | S2, L2 | ☐ |
| 25 | Excessive logging allocations | [.NET runtime](engineering-standards.md#net-runtime), [Observability](engineering-standards.md#observability): source-generated logging, sampling | S2 | ☐ |

### ThreadPool and concurrency

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 26 | ThreadPool starvation | [.NET runtime](engineering-standards.md#net-runtime): no sync-over-async; queue-length alert | S1, L2, O1 | ☐ |
| 27 | Excessive Task creation | [Load control](engineering-standards.md#load-control): bounded concurrency | S5, L3 | ☐ |
| 28 | Unbounded `Task.WhenAll` | [Load control](engineering-standards.md#load-control): `Parallel.ForEachAsync` with limit or bounded channel | S5 | ☐ |
| 29 | Lock contention | [.NET runtime](engineering-standards.md#net-runtime): no locks on hot path; [Ledger](architecture.md#ledger): append-only avoids shared rows | L1 (lock-contention counter) | ☐ |
| 30 | Deadlocks | [.NET runtime](engineering-standards.md#net-runtime): no sync-over-async; [Database](engineering-standards.md#database): consistent row order | L1, C6 | ☐ |
| 31 | Incorrect async locking | [.NET runtime](engineering-standards.md#net-runtime): `SemaphoreSlim` only, never `lock` around `await` | S5 | ☐ |
| 32 | Cancellation ignored | [Load control](engineering-standards.md#load-control): `CancellationToken` in every call, CA2016 | S5, C1 | ☐ |
| 33 | Race conditions | [Transition rules](payment-lifecycle.md#transition-rules): conditional updates; [Data model](architecture.md#data-model): constraints, not in-memory checks | C5, C6, U2 | ☐ |
| 34 | Excessive thread creation | [.NET runtime](engineering-standards.md#net-runtime): no `new Thread` or `Task.Run` in request path | S1 | ☐ |
| 35 | CPU-bound work on ThreadPool | [.NET runtime](engineering-standards.md#net-runtime): CPU-heavy work in separate workers | L1 | ☐ |

### Database

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 36 | Connection pool exhaustion | [Database](engineering-standards.md#database): connection budget, short wait timeout, fast failure | C2 | ☐ |
| 37 | Too many open connections | [Database](engineering-standards.md#database): PgBouncer with fixed per-cell pool | C2, O3 | ☐ |
| 38 | Slow EF Core queries | [Database](engineering-standards.md#database), [.NET runtime](engineering-standards.md#net-runtime): Dapper on hot path; `EXPLAIN` gate | S3 | ☐ |
| 39 | N+1 queries | [Database](engineering-standards.md#database): query-count tests | S3 | ☐ |
| 40 | Change tracking overhead | [.NET runtime](engineering-standards.md#net-runtime): `AsNoTracking` for reads | S3, S5 | ☐ |
| 41 | Large `DbContext` lifetime | [.NET runtime](engineering-standards.md#net-runtime): scoped `DbContext`, never in singletons | S5 | ☐ |
| 42 | `DbContext` concurrent access | [.NET runtime](engineering-standards.md#net-runtime): one connection and context per operation | S5 | ☐ |
| 43 | Lock contention | [Database](engineering-standards.md#database): short transactions, `lock_timeout`; [Ledger](architecture.md#ledger): no hot rows | L1, C6 | ☐ |
| 44 | Deadlocks | [Database](engineering-standards.md#database): consistent ordering, bounded retry on 40P01 | C6, L1, C34 | ☐ |
| 45 | Long-running transactions | [Database](engineering-standards.md#database): `statement_timeout`, idle-in-transaction timeout; no PSP call in a transaction | L2, C1 | ☐ |
| 46 | Database CPU saturation | [Database](engineering-standards.md#database): 60% CPU target with headroom; alerts | L1, O3 | ☐ |
| 47 | Database I/O saturation | [Database](engineering-standards.md#database): NVMe, separate WAL volume; commit measurements | L1, O3 | ☐ |
| 48 | WAL / transaction log growth | [Database](engineering-standards.md#database): archiving, replication-slot and disk alerts | C22, O1 | ☐ |
| 49 | Primary database failure | [Database](engineering-standards.md#database): synchronous standbys with quorum, automatic failover | C7 | ☐ |
| 50 | Replica lag | [Database](engineering-standards.md#database): fresh reads on primary; lag-aware replica routing | C7, C29 | ☐ |
| 51 | Index fragmentation and bloat | [Database](engineering-standards.md#database): partitioning, autovacuum tuning, `REINDEX CONCURRENTLY` | L2 | ☐ |
| 52 | Database migration locks | [Database](engineering-standards.md#database): expand/contract, `lock_timeout`, concurrent indexes | S4 | ☐ |
| 53 | Hot rows | [Ledger](architecture.md#ledger): append-only ledger, 64 sub-accounts, no balance updates on payment path | L1 (hot-merchant scenario) | ☐ |
| 54 | Excessive ORM-generated SQL | [Database](engineering-standards.md#database): generated SQL reviewed; Dapper on write path | S3 | ☐ |
| 55 | Transaction retry storms | [Database](engineering-standards.md#database): retries at most twice with jitter and a retry budget | C1, C7 | ☐ |

### Kafka and asynchronous processing

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 56 | Kafka broker crashes | [Events](architecture.md#events-and-webhooks): replication factor 3, `min.insync.replicas=2`, `acks=all` | C8 | ☐ |
| 57 | Consumer lag | [Events](architecture.md#events-and-webhooks), [Kubernetes](engineering-standards.md#kubernetes-and-operations): lag-based autoscaling, lag alert | L1, O1, CK6 | ☐ |
| 58 | Consumer crashes | [Events](architecture.md#events-and-webhooks): offsets committed after durable work | C17 | ☐ |
| 59 | Duplicate messages | [Events](architecture.md#events-and-webhooks): inbox table dedupe by `event_id` | C17, U4 | ☐ |
| 60 | Out-of-order events | [Events](architecture.md#events-and-webhooks): key by `payment_id`, per-payment sequence, stale events ignored | C9, U2 | ☐ |
| 61 | Poison messages | [Events](architecture.md#events-and-webhooks): retry then dead-letter queue with alert | C18 | ☐ |
| 62 | Consumer rebalancing | [Events](architecture.md#events-and-webhooks): cooperative-sticky, static membership | C17 | ☐ |
| 63 | Producer bottleneck | [Events](architecture.md#events-and-webhooks): batched outbox publisher, compression | L1 | ☐ |
| 64 | Event schema changes | [Events](architecture.md#events-and-webhooks): schema registry with backward compatibility | U5 | ☐ |
| 65 | Message acknowledged too early | [Events](architecture.md#events-and-webhooks): commit offset only after durable processing | C17 | ☐ |
| 66 | Kafka unavailable | [Events](architecture.md#events-and-webhooks): transactional outbox | C8 | ☐ |
| 67 | Consumer memory growth | [Events](architecture.md#events-and-webhooks), [Load control](engineering-standards.md#load-control): bounded prefetch and channels | L2 | ☐ |
| 68 | Excessive partitions | [Events](architecture.md#events-and-webhooks): partition plan per cell reviewed | O3 | ☐ |
| 69 | Large messages | [Events](architecture.md#events-and-webhooks): 32 KB event limit, IDs not blobs | U5 | ☐ |

### External payment provider

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 70 | PSP timeout | [PSP integration](architecture.md#psp-integration): `UNKNOWN` state, recovery by reconciler | C3, C44 | ☐ |
| 71 | PSP outage | [PSP integration](architecture.md#psp-integration): per-PSP breaker, failover to secondary | C13 | ☐ |
| 72 | PSP rate limiting | [PSP integration](architecture.md#psp-integration): client-side token bucket, 429 handling | C12 | ☐ |
| 73 | Duplicate authorization | [PSP integration](architecture.md#psp-integration): `attempt_id` as PSP idempotency key | C3, C5 | ☐ |
| 74 | Delayed callbacks | [PSP integration](architecture.md#psp-integration): reconciler polls stuck payments | C9, CK5 | ☐ |
| 75 | Duplicate callbacks | [PSP integration](architecture.md#psp-integration): `psp_events` primary key dedupe | C9 | ☐ |
| 76 | Provider API changes | [PSP integration](architecture.md#psp-integration): versioned adapters, recorded-fixture contract tests | U5 | ☐ |
| 77 | TLS / certificate issues | [Kubernetes](engineering-standards.md#kubernetes-and-operations): expiry alerts, cert-manager | C16, C21 | ☐ |
| 78 | DNS failures | [PSP integration](architecture.md#psp-integration): connection lifetime and DNS refresh; retry on connect failure | C16 | ☐ |
| 79 | Incorrect retry | [PSP integration](architecture.md#psp-integration): retry only with the same idempotency key; attempt recorded before call | C3, C4 | ☐ |

### Financial correctness and ledger

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 80 | Duplicate charges | [Payment flow](architecture.md#payment-flow), [Data model](architecture.md#data-model): idempotency key unique constraint, one-live-attempt indexes and PSP idempotency | C5, C36, C37, C38, C41, CK2 | ☐ |
| 81 | Lost payment records | [Payment flow](architecture.md#payment-flow): intent written before PSP call; synchronous replication | C4, C7 | ☐ |
| 82 | Duplicate ledger entries | [Ledger](architecture.md#ledger): `source_event_id` unique; `(txn_id, entry_no)` primary key | C17, U3, CK1 | ☐ |
| 83 | Unbalanced ledger | [Ledger](architecture.md#ledger): single-statement insert, deferred trigger backstop, audit query | U3, CK1 | ☐ |
| 84 | Concurrent refunds | [Refund rules](payment-lifecycle.md#refund-rules): conditional update and `CHECK` constraint | C6, CK3 | ☐ |
| 85 | Incorrect decimal arithmetic | [Data model](architecture.md#data-model): integer minor units, no floating point | U1 | ☐ |
| 86 | Currency precision errors | [Data model](architecture.md#data-model): currency exponent and rounding rule table | U1 | ☐ |
| 87 | Negative merchant balance | [Ledger](architecture.md#ledger): can't occur in v1 ([D38](decisions.md#d38)); per-payment limit with a reservation entry. C35 returns with fees or payouts | U3, C6 | ☐ |
| 88 | Payment succeeds but ledger fails | [Ledger](architecture.md#ledger): same transaction; cannot occur by construction | C15, C4 | ☐ |
| 89 | Refund succeeds but response times out | [Refund rules](payment-lifecycle.md#refund-rules), [PSP integration](architecture.md#psp-integration): refund idempotency key; PSP refund key; inquiry | C3, C6 | ☐ |
| 90 | Historical ledger mutation | [Ledger](architecture.md#ledger): append-only, update/delete revoked, reversing entries | O4, U3, CK1 | ☐ |

### Kubernetes, infrastructure and networking

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 91 | Container OOMKilled | [Kubernetes](engineering-standards.md#kubernetes-and-operations), [.NET runtime](engineering-standards.md#net-runtime): memory limit above GC hard limit | L2 | ☐ |
| 92 | CPU throttling | [Kubernetes](engineering-standards.md#kubernetes-and-operations): integer CPU requests equal to limits; throttling metric | L1, O1 | ☐ |
| 93 | Node failure | [Kubernetes](engineering-standards.md#kubernetes-and-operations): topology spread, PodDisruptionBudgets | C10 | ☐ |
| 94 | Availability zone failure | [Cells](architecture.md#cells-and-routing), [Kubernetes](engineering-standards.md#kubernetes-and-operations): each cell spans 3 zones, with standbys in different zones; spare cell if cells are adopted | C11 | ☐ |
| 95 | Autoscaling too slowly | [Kubernetes](engineering-standards.md#kubernetes-and-operations): HPA on in-flight requests plus 40% headroom | L3 | ☐ |
| 96 | Bad deployment | [Kubernetes](engineering-standards.md#kubernetes-and-operations): canary with automatic rollback | C19 | ☐ |
| 97 | Incorrect readiness probe | [Kubernetes](engineering-standards.md#kubernetes-and-operations): separate liveness and readiness; readiness reflects dependencies | C20, C7 | ☐ |
| 98 | Graceful shutdown failure | [Kubernetes](engineering-standards.md#kubernetes-and-operations): drain via `IHostApplicationLifetime`; outbox drained | C20 | ☐ |
| 99 | Disk exhaustion | [Database](engineering-standards.md#database), [Kubernetes](engineering-standards.md#kubernetes-and-operations): alerts at 70% and 85% | C22 | ☐ |
| 100 | Secret expiration | [Kubernetes](engineering-standards.md#kubernetes-and-operations): rotation and 30-day expiry alerts | C21 | ☐ |

### Security, fraud and compliance

| # | Production problem | Design response | Proof | Verified |
|---|---|---|---|---|
| 101 | Stolen API credentials | [Security](engineering-standards.md#security-and-fraud): hashed scoped keys, roll with grace period, anomaly alerts | O4, U7 | ☐ |
| 102 | Card testing attacks | [Security](engineering-standards.md#security-and-fraud): velocity rules on Redis counters | F1 | ☐ |
| 103 | Account takeover | [Security](engineering-standards.md#security-and-fraud): MFA and step-up for key roll and payouts | O4 | ☐ |
| 104 | Replay attacks | [Security](engineering-standards.md#security-and-fraud): signed requests and webhooks with timestamp; idempotency | U7, U4 | ☐ |
| 105 | Cross-merchant access | [Security](engineering-standards.md#security-and-fraud): `merchant_id` on every tenant-owned table, row-level security backstop | U6 | ☐ |
| 106 | Sensitive information in logs | [Security](engineering-standards.md#security-and-fraud): redaction; card data never reaches us ([D39](decisions.md#d39)) | U8 | ☐ |
| 107 | Unauthorized financial operations | [Security](engineering-standards.md#security-and-fraud): scoped keys, RBAC, append-only audit log | O4, U6 | ☐ |
| 108 | Fraud service unavailable | [Security](engineering-standards.md#security-and-fraud): explicit fallback policy per risk tier | C14 | ☐ |

## Production readiness checklist

| # | Item | Where | Proof | Done |
|---|---|---|---|---|
| 1 | Rate limiting and bounded concurrency | [Load control](engineering-standards.md#load-control) | L3 | ☐ |
| 2 | No sync-over-async in request paths | [.NET runtime](engineering-standards.md#net-runtime) | S1 | ☐ |
| 3 | ThreadPool starvation testing | [.NET runtime](engineering-standards.md#net-runtime), [Observability](engineering-standards.md#observability) | L2, C1 | ☐ |
| 4 | GC and allocation profiling | [.NET runtime](engineering-standards.md#net-runtime) | S2, L2, O2 | ☐ |
| 5 | Task lifecycle and memory monitoring | [.NET runtime](engineering-standards.md#net-runtime), [Observability](engineering-standards.md#observability) | L2, O1 | ☐ |
| 6 | Graceful shutdown | [Kubernetes](engineering-standards.md#kubernetes-and-operations) | C20 | ☐ |
| 7 | Connection pool sizing | [Database](engineering-standards.md#database) | C2, O3 | ☐ |
| 8 | Query optimization (Dapper hot path, EF read side) | [Database](engineering-standards.md#database) | S3 | ☐ |
| 9 | Deadlock and lock contention tests | [Database](engineering-standards.md#database) | L1, C6, C34 | ☐ |
| 10 | Database HA failover | [Database](engineering-standards.md#database) | C7 | ☐ |
| 11 | Transactional outbox | [Events](architecture.md#events-and-webhooks) | C8, CK4 | ☐ |
| 12 | Broker consumer recovery | [Events](architecture.md#events-and-webhooks) | C17, C18 | ☐ |
| 13 | Bounded channels and backpressure | [Load control](engineering-standards.md#load-control) | L3 | ☐ |
| 14 | End-to-end idempotency | [Payment flow](architecture.md#payment-flow), [Data model](architecture.md#data-model) | C5, U4, CK2 | ☐ |
| 15 | Double-entry ledger | [Ledger](architecture.md#ledger) | U3, CK1 | ☐ |
| 16 | Atomic refund limits | [Refund rules](payment-lifecycle.md#refund-rules) | C6, CK3 | ☐ |
| 17 | Unknown payment state handling | [Transition rules](payment-lifecycle.md#transition-rules), [PSP integration](architecture.md#psp-integration) | C3, C4, CK5 | ☐ |
| 18 | Provider recovery mode | [PSP integration](architecture.md#psp-integration) | C3, C44, C45 | ☐ |
| 19 | Reconciliation | [PSP integration](architecture.md#psp-integration) | C9, CK5 | ☐ |
| 20 | Multi-AZ Kubernetes deployment | [Cells](architecture.md#cells-and-routing), [Kubernetes](engineering-standards.md#kubernetes-and-operations) | C10, C11 | ☐ |
| 21 | Autoscaling with headroom | [Kubernetes](engineering-standards.md#kubernetes-and-operations) | L3 | ☐ |
| 22 | Circuit breakers and bulkheads | [PSP integration](architecture.md#psp-integration) | C13 | ☐ |
| 23 | Canary deployments | [Kubernetes](engineering-standards.md#kubernetes-and-operations) | C19 | ☐ |
| 24 | Security and PCI DSS controls | [Security](engineering-standards.md#security-and-fraud) | O4, U6, U8 | ☐ |
| 25 | 5,000 TPS load and failure testing | [Verification](#how-verification-works) | L1-L6, C1-C51 (C33 only if cells are adopted; C35 only with fees or payouts) | ☐ |

## Progress

Update these counts as rows are ticked.

| Group | Count | Verified |
|---|---|---|
| Production problems | 108 | 0 |
| Production readiness items | 25 | 0 |
| [Failure scenarios](failure-scenarios.md#failure-catalogue) (FS-01 to FS-51) | 51 | 0 |
| [Edge cases](failure-scenarios.md#edge-cases) (E1 to E29) | 29 | 0 |
| [Runbooks](failure-scenarios.md#runbooks) (R1 to R7) | 7 | 0 (none rehearsed) |

A scenario is verified only when its proof test has passed with a recorded run, and its runbook, if any, has been rehearsed. The [recovery flows](failure-scenarios.md#recovery-flows) show each scenario's path; a diagram is a design claim, and the proof test is still what verifies it.

## Build phases

| Phase | Build | Exit when these pass |
|---|---|---|
| 0 | Mock PSP, k6 load scripts, automated CK1-CK10 checks, dashboards | CK checks run automatically after every test |
| 1 | Payments core, idempotency, ledger, refunds, outbox table (written in the same transaction from the start), canonical log line | U1-U4, C5, C6, C31a, C43, S1, S3, S5, S6 |
| 2 | PSP adapter, reconciler, callbacks, settlement ingestion | C3, C4, C9, C12, C13, C15, C16, C23, C26, C31b, C32, C34, C36, C37, C38, C39, C40, C41, C44, C45, C46, C47, C50, C51, U5 |
| 3 | Outbox publisher, broker, consumers, webhooks | C8, C17, C18, C27, C31c, U5, CK6 |
| 4 | Hardening: limits, HA, deployment, minimal synchronous fraud, edge gateway, client-side access, and cells only if gate G1 requires them | L1-L6, C1, C2, C7, C10, C11, C19-C22, C24, C25, C28-C30, C33 (cells only), C48, C49, S2, S4, O3 |
| 5 | Security, full fraud rules, operations, test mode isolation | U6-U8, F1, C14, C42, O1, O2, O4, O5, O6, then L5 again on the complete path |

**Sign-off:** all 108 and all 25 checklist rows are ticked, each with a recorded run, and the L5 run after Phase 5 is the number reported ([D24](decisions.md#d24)). Live card traffic also needs a PCI assessor's confirmation of scope ([D39](decisions.md#d39)).
