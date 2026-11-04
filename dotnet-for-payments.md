# Why .NET Fits a Payment System (Open Payments)

Status: draft v1 · Date: 2026-11-04 · Companion to `payment-system-design.md`

## 1. Short answer

.NET is a strong choice for a payment system because it combines four things that this kind of system needs:

1. **Efficient handling of many waiting requests.** A payment spends most of its life waiting on a bank or a database. .NET's async model lets one process hold thousands of waiting requests cheaply.
2. **Strong typing.** The compiler catches whole classes of money bugs (wrong currency, missing value, wrong state) before the code runs.
3. **A mature, supported toolbox.** Web server, database drivers, resilience, rate limiting, telemetry and profiling are built in or well established.
4. **Good tooling and hiring.** Analyzers, benchmarks, profilers and a large developer pool reduce delivery risk.

"Excellent" here means **well suited**, not "better than every alternative". Section 5 lists the honest trade-offs. And like every claim in this project, performance is **not proven** until the load tests in `scaling-playbook.md` pass.

## 2. Why each strength matters for payments

### 2.1 Async I/O fits the shape of a payment

At 5,000 requests per second with a bank taking about 500 ms, roughly 2,500 requests are waiting at any moment (Little's Law). In .NET, `async`/`await` frees the thread while a request waits, so those waiting requests do not each need a thread. ASP.NET Core's Kestrel server is built for this.

The catch is that this only works if the code never blocks a thread. The design forbids `.Result`, `.Wait()` and `.GetAwaiter().GetResult()` and enforces it in the build (design section 13, gate S1).

### 2.2 The type system helps with money

- **Integer minor units.** The design stores money as integers (cents) with a per-currency exponent, not floating point. C# makes it easy to wrap that in a small `Money` type so an amount cannot be added to a different currency by accident.
- **`decimal`** exists for display and intermediate calculations where needed, but the stored form stays integer.
- **Records and enums** model payment states clearly. A `switch` over the state can warn when a state is not handled.
- **Nullable reference types** turn "forgot to check for null" into a compiler warning.
- **Immutability** (`record`, `readonly`, `init`) suits append-only data such as ledger entries and events.

None of this makes the code correct by itself. It narrows the places where mistakes can hide, and the property tests (U1 to U3) still have to prove the rules.

### 2.3 Built-in and established building blocks

| Need in the design | .NET option | Design section |
|---|---|---|
| High-throughput HTTP server | ASP.NET Core with Kestrel | 12.4 |
| Rate limiting and concurrency limiting | Built-in rate-limiting middleware (fixed window, token bucket, concurrency) | 12 |
| Time limits and cancellation | `CancellationToken`, request timeouts | 12.3 |
| Retries, circuit breaker, timeouts to the PSP | Polly or `Microsoft.Extensions.Resilience` | 10 |
| Bounded queues in the process | `System.Threading.Channels`, `SemaphoreSlim` | 12.2 |
| PostgreSQL access | Npgsql (driver), Dapper for hot paths, EF Core for the rest | 14 |
| Kafka client | Confluent.Kafka | 11 |
| Background workers (outbox publisher, reconciler) | `BackgroundService` / hosted services | 4, 11 |
| Health probes for Kubernetes | Health checks (liveness and readiness) | 15 |
| Metrics, traces, logs | OpenTelemetry for .NET, `System.Diagnostics.Metrics`, structured logging | 17 |
| Graceful shutdown | Generic host with a shutdown timeout | 15 |
| Secrets and signing | Data protection APIs, `HMACSHA256`, `CryptographicOperations.FixedTimeEquals` | 16 |

Library names and licenses change over time. Check each one before adopting it (see section 5).

### 2.4 Diagnosing production problems

Many of the 108 production problems are about memory, threads and garbage collection. .NET has strong tools for exactly these:

- `dotnet-counters` (live metrics), `dotnet-trace` (CPU and event traces), `dotnet-dump` (heap analysis).
- Runtime metrics: Gen 0/1/2 collections, GC pause time, ThreadPool queue length, lock contention.
- BenchmarkDotNet for allocation and speed budgets on the hot path (gate S2).
- Roslyn analyzers that fail the build on dangerous patterns (gate S1, S5).

These are the same tools the runbooks in the design (drill O2) rely on.

### 2.5 Garbage collection is manageable for this workload

The design uses Server GC and an allocation budget on the hot path. A payment request is short-lived, so most objects die young and are cheap to collect. The risk is a large number of long-lived objects or big buffers causing Gen 2 collections and pauses. Test L2 (24-hour soak) watches for exactly this.

### 2.6 Deployment and operations

- Runs on Linux in small container images, which fits the Kubernetes plan.
- Fast startup compared with older stacks, which helps autoscaling and rollouts, though it still needs warm-up testing.
- Regular releases with clear support windows (see section 6).

### 2.7 Testing and engineering practice

- xUnit or NUnit for tests, Testcontainers for real PostgreSQL and Kafka in tests, and property-based testing libraries for money and ledger rules (U1 to U3).
- Mature IDEs, debuggers and refactoring tools lower the cost of keeping a long-lived codebase clean.

### 2.8 People and ecosystem

- A large pool of C# developers, and many teams in banking and finance already run .NET, so hiring and shared knowledge are easier than for newer languages.
- Strong documentation and long-term backwards compatibility lower the risk of surprise rewrites.

## 3. Where .NET helps with the hardest problems in this design

| Hard problem | How .NET helps | What still must be proven |
|---|---|---|
| Thousands of waiting requests | Async I/O, Kestrel | L1 to L6 (no ThreadPool starvation) |
| Overload | Built-in concurrency limiter, bounded channels | L3, L6, C1 |
| Slow or failing bank | Resilience libraries, deadlines, cancellation | C3, C12, C13, C23 |
| Money precision | Integer minor units, strong types | U1, U3 |
| Duplicate requests | Unique constraints with Npgsql and clear exceptions for violations | C5, U4 |
| Memory and GC trouble | Runtime metrics, dump and trace tools | L2, O2 |
| Safe rollouts | Health checks, graceful shutdown | C19, C20 |

## 4. How the language compares (general judgement)

This is a general comparison, not a benchmark. The right choice also depends on the team, the budget and the existing systems.

| | .NET (C#) | Java / Kotlin | Go | Rust |
|---|---|---|---|---|
| Throughput for I/O-heavy APIs | High | High | High | Very high |
| Latency control (pauses) | Good with tuning | Good with tuning (modern collectors are strong) | Good, simple GC | Best (no GC) |
| Memory use per instance | Medium | Medium to high | Low | Lowest |
| Type safety for money rules | Strong | Strong | Moderate (simpler type system) | Very strong |
| Payment-related open source | Smaller | Largest (for example Kill Bill) | Some (for example Formance Ledger) | Growing (for example Hyperswitch) |
| Hiring pool | Large | Very large | Large | Smaller |
| Learning curve | Moderate | Moderate | Low | High |

A fair summary: Java is the closest peer. Go and Rust can use less memory per instance, and Rust gives the tightest control over latency, but at a higher cost in learning and hiring. .NET sits in a comfortable middle for a team that already knows C#.

## 5. Honest trade-offs and risks

- **GC pauses are possible.** They are manageable with Server GC, allocation budgets and the soak test, but a language without a garbage collector avoids the risk entirely.
- **Thread-blocking mistakes are easy to make.** One `.Result` in a hot path can cause ThreadPool starvation under load. The design guards against it with build gates, but it takes discipline.
- **EF Core can hide cost.** N+1 queries and tracking overhead hurt at this volume. The design uses Dapper on the hot path and `AsNoTracking` elsewhere (gate S3).
- **Smaller payments ecosystem.** There are fewer ready-made payment engines in C# than in Java. That is why the project looks at open-source parts such as Hyperswitch, Formance Ledger and TigerBeetle, which are not written in C#.
- **Library licenses can change.** Several popular .NET libraries have moved to commercial licenses in recent years. Run the license scan (gate S6) before adopting any package, and check the license of every library named in this document.
- **Memory per instance is higher than Go or Rust,** which can raise the cost at large scale.
- **Language choice is rarely the first bottleneck.** The database, the bank and the design (idempotency, hot rows, queues) usually limit throughput before the language does.

## 6. Version choice: check this before starting

The design document says .NET 8. According to Microsoft's support policy, .NET 8 and .NET 9 reach end of support on **10 November 2026**, which is days from the date of this document. A new project should target the current long-term-support release (.NET 10) so it starts inside its support window.

Action: confirm the dates on the official policy page, then update the "Stack" line in the design and the build image versions. Re-check that every library supports the chosen version.

Source: [.NET and .NET Core Support Policy](https://dotnet.microsoft.com/en-US/platform/support/policy/dotnet-core)

## 7. When .NET would not be the best choice

- The team has no C# experience and strong experience in another stack that meets the same gates.
- The hot path needs latency control so tight that any garbage collection is unacceptable. This is unusual for a payment API, where the bank's time dominates.
- The project decides to adopt a Rust or Java payment core (for example Hyperswitch or Kill Bill). Then .NET may be only the surrounding services, or not used at all.

## 8. How to keep the choice honest

The language decision is a hypothesis. These steps test it cheaply:

1. Build the vertical slice (payment create, ledger, outbox) in C# and run the database spike first. If the database is the first limit, language is not the issue.
2. Run the ramp in `scaling-playbook.md` and record the first bottleneck at each step.
3. Check Gen 2 GC rate, ThreadPool queue and allocation per request in tests L2 and L5.
4. If CPU or GC is the first limit and cannot be tuned away, revisit the choice for that one component only (for example the hot path), not the whole system.

## 9. Summary

.NET is a good fit for payments because payments are waiting-heavy, correctness-heavy and operations-heavy, and .NET handles all three well with strong types, async I/O, built-in limits and good diagnostic tools. It is not the only good choice, and the claim is proven only when the load and failure tests pass.
