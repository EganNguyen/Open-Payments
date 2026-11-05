# Engineering Standards

The rules for load control, .NET code, the database, the platform, security and observability that the architecture depends on.

Status: Draft · Last updated: 2026-11-05

All values are starting points. L1 and O3 replace them with measured values.

## Load control

**Layers:**

1. Edge: per-merchant token-bucket rate limit.
2. Gateway and API: global concurrency limiter with no queue.
3. Per instance: an in-flight cap, starting at 500 and tuned by L1 and L3.
4. Downstream: a rate limiter per PSP account and per-dependency bulkheads, with separate limits for requests, PSP calls, database work and callbacks, and a recovery quota inside each PSP's limit ([D34](decisions.md#d34)).

Over the limit, the system rejects immediately with 429 or 503 and `Retry-After`. Waiting in an unbounded queue is never allowed.

```csharp
// illustrative; values come from load tests
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status503ServiceUnavailable;
    o.AddConcurrencyLimiter("payments", c =>
    {
        c.PermitLimit = 500;
        c.QueueLimit = 0; // reject instead of queueing
    });
});
```

**Bounded concurrency in the process.** Fan-out work (webhook sends, event handling) uses a bounded `Channel<T>` with fixed workers, or `Parallel.ForEachAsync` with `MaxDegreeOfParallelism`. No fire-and-forget `Task.Run`, and no unbounded `Task.WhenAll` over input-sized lists.

**Deadlines.** A request has a total budget of 5 s: fraud 50 ms, PSP up to 2.5 s, each database call 200 ms. The attempt timeout (starting at 30 s) stays above all of these and below the PSP's key retention ([D34](decisions.md#d34)). The `CancellationToken` is passed into every async call (analyzer CA2016 is enabled), so abandoned work stops, with two exceptions: `COMMIT` is never cancelled ([D28](decisions.md#d28)), and once transaction A has committed, the request's token gives way to a deadline token.

**Kestrel limits:**

```csharp
builder.WebHost.ConfigureKestrel(o =>
{
    o.Limits.MaxRequestBodySize = 64 * 1024;
    o.Limits.MaxConcurrentConnections = 10_000;
    o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
    o.Limits.MinRequestBodyDataRate = new MinDataRate(240, TimeSpan.FromSeconds(5));
    o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(60);
});
```

Load balancer idle timeouts are longer than the Kestrel keep-alive, so connections are not reset mid-request.

## .NET runtime

**Async and threads:**

- The request path is async end to end. An analyzer fails the build on `.Result`, `.Wait()` and `.GetAwaiter().GetResult()` in request-path projects, and on synchronous database and HTTP APIs.
- No `lock` around an `await`. Use `SemaphoreSlim` for async mutual exclusion, and avoid locks on the hot path entirely.
- No `new Thread` or `Task.Run` in the request path.
- CPU-heavy work (batch signing, large hashing) runs in separate worker processes.

**Memory and GC:**

- Server GC with concurrent GC. `DOTNET_GCHeapHardLimitPercent` is set below the container limit, so the GC reacts before the OOM killer.
- Each hot-path request has an allocation budget enforced by BenchmarkDotNet in CI. Measure first, then optimize.
- Optimize only measured hot spots: `System.Text.Json` source generation, no LINQ or closures in hot loops, `Span<T>`, `ArrayPool<byte>` for large temporary buffers, `LoggerMessage` source-generated logging with sampling, `Money` as a struct to avoid boxing.
- Payload limits keep hot-path objects under the 85 KB large-object-heap threshold.
- Caches are bounded (`MemoryCache` with `SizeLimit`), and static collections always have a bound.
- `IDisposable` analyzers (such as CA2000) are on. Use `using` and `await using`.
- No pinning unless unavoidable.

**Data access:**

- Hot write path: Dapper with parameterized SQL, one connection per operation.
- EF Core only for dashboard reads, with `AsNoTracking`, a scoped `DbContext`, no `DbContext` in singletons, and no concurrent use of one context.

**HTTP clients:** `IHttpClientFactory` with `SocketsHttpHandler` and `PooledConnectionLifetime` of a few minutes, so DNS changes are picked up. `HttpClient.Timeout` is infinite, and the deadline comes from `CancellationToken`. Certificates are monitored for expiry.

## Libraries

Check each library's license and .NET 10 support before adopting it (gate S6).

| Need | Option |
|---|---|
| HTTP server | ASP.NET Core with Kestrel |
| Rate and concurrency limiting | Built-in rate-limiting middleware |
| Time limits and cancellation | `CancellationToken`, request timeouts |
| PSP resilience | Polly v8 or `Microsoft.Extensions.Resilience` |
| Bounded queues | `System.Threading.Channels`, `SemaphoreSlim` |
| PostgreSQL access | Npgsql, Dapper for hot paths, EF Core for dashboard reads |
| Kafka client | Confluent.Kafka |
| Background workers | `BackgroundService` |
| Kubernetes probes | Health checks |
| Metrics, traces, logs | OpenTelemetry for .NET, `System.Diagnostics.Metrics`, structured logging |
| Graceful shutdown | Generic host with a shutdown timeout |
| Signing | `HMACSHA256`, `CryptographicOperations.FixedTimeEquals`, data protection APIs |
| Profiling | `dotnet-counters`, `dotnet-trace`, `dotnet-dump`, BenchmarkDotNet |
| Testing | xUnit or NUnit, Testcontainers, a property-based testing library |

## Database

**Connection budget:**

- The application connects to PgBouncer in transaction-pooling mode. PgBouncer holds a fixed pool per cell, starting at about 100 and tuned in L1.
- At each scale-out, check that `instances × Npgsql MaxPoolSize` fits PgBouncer's client limit, and that PgBouncer's pool fits `max_connections` with headroom for admin.
- A short pool wait timeout (for example 200 ms) turns exhaustion into fast failure.
- Transaction pooling keeps no session state. PgBouncer 1.21 and later [can track protocol-level prepared statements](https://manpages.ubuntu.com/manpages/questing/man1/pgbouncer.1.html). Verify that with Npgsql in the spike before relying on it.
- [Each PgBouncer process uses one core](https://clickhouse.com/blog/pgbouncer-clickhouse-managed-postgres.md). Run several with `so_reuseport` if the spike shows one is the limit, and divide the connection budget between them.

**Transactions:**

- At most about 5 statements, targeting a few milliseconds. No PSP, HTTP or broker call inside a transaction.
- Application role settings (API, reconciler, timers): `lock_timeout` 200 ms, `statement_timeout` 1 s, `idle_in_transaction_session_timeout` 2 s.
- Server setting: `deadlock_timeout` 100 ms. It must stay below `lock_timeout` so deadlocks are detected instead of surfacing as lock timeouts ([D1](decisions.md#d1)).
- Touch rows in a consistent order.
- On `40001` or `40P01`, retry at most twice with jitter, inside a retry budget. On `55P03`, do not retry: return 503 with `Retry-After`. See [Handling a deadlock](deadlock-handling.md#handling-a-deadlock).

**Queries:** query-count tests per endpoint fail CI on N+1 patterns. `EXPLAIN` checks for hot queries run in CI against a representative dataset. Generated EF Core SQL is logged and reviewed.

**Capacity:** target at most about 60% CPU on the primary at the target load. Use NVMe storage, with WAL on its own volume. Synchronous replication adds a network round trip to every commit; L1 records the cost.

**Maintenance:**

- Partition append-heavy tables as in the [data model](architecture.md#data-model). Old outbox partitions are dropped, not deleted row by row.
- Tune autovacuum for hot tables and monitor index bloat, using `REINDEX CONCURRENTLY` when needed.
- WAL archiving is on. Replication slot lag and disk usage alert at 70% and 85%.

**High availability:**

- Patroni or a managed multi-AZ service, with two synchronous-capable standbys in different zones and quorum `ANY 1` ([D7](decisions.md#d7)). A committed transaction survives the loss of the primary or of any one zone.
- Reads that must be fresh (payment status, refund checks, idempotency checks) go to the primary. Replicas serve dashboard and reporting reads, with lag-aware routing.

**Migrations:** expand, then contract in separate releases. Use a `lock_timeout` of about 2 s, `CREATE INDEX CONCURRENTLY`, no table rewrites during deploys, and small-batch backfills.

## Kubernetes and operations

- Each cell runs at least 3 API replicas across 3 availability zones (topology spread constraints), with PodDisruptionBudgets.
- HPA scales on in-flight requests per pod and consumer lag, plus about 40% standing headroom. Cap max replicas so that `max replicas × connections per pod` stays inside the connection budget.
- The memory limit leaves room above the GC hard limit. API pods use integer CPU requests equal to limits, and throttling is monitored.
- **Probes:** startup (app initialized), liveness (process only, never the database), readiness (database reachable, at least one PSP breaker not open, not draining).
- **Graceful shutdown:** on SIGTERM readiness turns false, the app stops accepting requests, drains in-flight work through `IHostApplicationLifetime` (up to about 30 s), and publishes remaining outbox rows. `preStop` delay and `terminationGracePeriodSeconds` match.
- **Deployments:** canary 5%, then 25%, then 100%, with automatic rollback on error rate or p99 breach.
- **Secrets and certificates:** external secrets with rotation, expiry alerts 30 days ahead, cert-manager for TLS.
- **Disk:** alerts at 70% and 85% for databases, brokers and logs.

## Security and fraud

- **Credentials:** API keys are stored hashed, scoped (read, write, refund), optionally IP-restricted, and rolled with a grace period. Unusual usage raises alerts.
- **Tenant isolation:** `merchant_id` is on every tenant-owned table and in every query through a shared repository layer. PostgreSQL row-level security is a backstop (`SET LOCAL app.merchant_id` in each transaction, which works with transaction pooling).
- **Card data:** PSP-hosted fields collect card numbers, which never reach our systems. The payment database stores only PSP tokens ([D39](decisions.md#d39)). A PCI assessor confirms the scope before live card traffic.
- **PSP credentials:** each merchant's credentials and callback secrets live in external secrets, referenced from `psp_accounts`, and are never logged or stored in the payment database ([D35](decisions.md#d35)).
- **Logging:** redaction middleware, structured logs only, and tests that scan output for card-number patterns.
- **Replay protection:** signed webhooks carry a timestamp and are rejected outside a window. Requests are also protected by idempotency keys.
- **Dashboard access:** MFA, RBAC, step-up authentication for key rolling and payouts, and an append-only audit log.
- **Fraud rules:** synchronous velocity rules (per IP, card, merchant; card-testing patterns) run within the 50 ms budget on Redis counters. Heavy analysis is asynchronous.
- **Fraud fallback:** when the service is unavailable, the low-risk tier fails open up to an amount limit and is flagged for review. The high-risk tier and amounts above the limit fail closed.

## Observability

Thresholds are tuned after L1. OpenTelemetry tracing follows a payment from API through PSP call to consumer. Profiling runbooks are drilled in O2.

**Canonical log line** ([D23](decisions.md#d23)). Each request emits one wide line, in a block that always runs, and a failure building it never fails the request. Fields: `merchant_id`, `payment_id`, `attempt_id`, state from and to, limiter outcome, request class, PSP, breaker state, PSP wait, database time, pool wait and `Decide` rule version. Platform overhead is total time minus PSP wait. The log scan in U8 covers this line.

| Signal | Source | Alert |
|---|---|---|
| ThreadPool queue length and thread count | `dotnet-counters`, runtime metrics | Queue above threshold for 30 s |
| Gen 2 GC rate, allocation rate, GC pause | Runtime counters | Above baseline |
| Lock contention rate | Runtime counters | Sustained increase |
| Platform overhead p50, p95, p99 | ASP.NET Core instrumentation | p99 above 100 ms |
| Pool wait time and active connections | Npgsql metrics, PgBouncer stats | Wait above threshold |
| Outbox age (oldest unpublished row) | Database query | Older than 30 s |
| Consumer lag per group | Kafka exporter | Above threshold and growing |
| Breaker state, PSP error and timeout rate | Adapter metrics | Breaker open, rate above baseline |
| Unresolved payment, capture attempt and refund count and age, including oldest `PSP_PENDING` | Business metric | Older than reconciler threshold |
| Ledger imbalance | Continuous audit query | Any non-zero (pages) |
| Reconciliation discrepancies | Reconciler | Any non-zero (pages) |
| Admitted, rejected and completed rates | Gateway and API metrics | Completed diverges from admitted without matching rejections |
| Success, decline and idempotency conflict rates | Business metrics | Deviation from baseline |
| Payments per state | Business metric | Pile-ups, such as many in `UNKNOWN` or `REVIEW` |
| Transition rate by from and to state | Business metric | Abnormal flows |
| Ignored late or duplicate events | Callback receiver | Spike suggesting PSP problems |
| Conditional-update zero-row count | Payment-transition module | Rising contention or races |
| Authorization-to-capture ratio | Business metric | Deviation from baseline |
| Dispute rate (`Proposed`) | Business metric | Merchant and fraud risk |
| Disk, WAL and replication lag | Infrastructure | 70% and 85% disk, lag above threshold |
