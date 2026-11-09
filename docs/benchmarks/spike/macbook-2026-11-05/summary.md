# MacBook Spike Summary

What the database tier does on the owner's MacBook, what limits it, and what that implies for 5,000 payments/s in production. How to reproduce it is in the [benchmark guide](../../benchmark-guide.md).

Status: Database tier only, stopped early by owner's choice · **Doesn't answer G1, and doesn't prove production capacity** · Run date: 2026-11-05

## Summary

- **Clean durable result:** 1,497 scripts/s, or about **3,000 commits/s**, with p50 49 ms, p99 195 ms, no aborts and verification passing. That's one 60-second run.
- **Saturation:** 2,300-2,800 scripts/s, about 4,600-5,650 commits/s. This Mac can't reach the 17,499 commits/s required.
- **The limit is the SSD's flush,** which can't be fixed here without weakening durability. `WALWrite` and `SyncRep` are 80-91% of waits, with only about 5 cores busy.
- **Full platform on this Mac: about 650-750 payments/s, calculated, not measured,** because the platform wasn't built at the time. The platform has since been built and run on the Docker stack, whose flushes aren't durable: through step 5 (248 completed payments/s) valid, and step 6 (498/s) invalid from a generator limit, with no bottleneck named ([BASELINE](../../../../benchmarks/BASELINE.md)). That isn't comparable with this durable estimate, and no durable platform run exists yet.
- **5,000 payments/s in production is plausible with cells, and unproven.** One primary is not supported by this evidence.

## Environment

| Item | Value |
|---|---|
| Machine | Apple M5 Pro, 18 cores, 48 GB, one internal SSD, on AC power |
| Software | PostgreSQL 18.6, PgBouncer 1.26.1 native, 1.24 in Docker |
| Durability | Quorum commit `ANY 1` of 2 standbys; native runs flush with `F_FULLFSYNC` |
| Workload | 90 sales to 15 refunds, 2 commits each, skewed across 100 merchants, 5-7.5M payments prefilled, open-model arrivals |
| Measurement | 40-60 s per rate after a short warm-up, or 40 s at overload |

## Baseline, then each limit removed

All at overload, offering 10,000 scripts/s, so only achieved rate is meaningful. Each step changes one thing.

| Step | Stack | Scripts/s | Commits/s | Result |
|---|---|---|---|---|
| Starting point: 14 CPUs, 20 GB, default sizing | Docker, not durable | 2,027-2,078 | About 4,100 | PgBouncer about 0.8 core |
| 18 CPUs, 40 GB, tuned Postgres | Docker, not durable | 2,018 | 4,037 | **No gain,** PgBouncer 0.97 core |
| Generator inside the VM | Docker, not durable | 3,319 | 6,637 | Port forwarding was a limit; PgBouncer 0.99 core |
| 800 clients | Docker, not durable | 3,022 | 6,045 | No gain |
| 4 PgBouncers, pool 100 | Docker, not durable | **4,880, 4,866** | **About 9,750** | Best overall; `BufferContent` appears |
| **Real flushes,** pool 100 | **Native, durable** | **1,616** | **3,232** | `WALWrite` 54%, `SyncRep` 34%, 2.8 cores; clients aborted |
| Pool 400 | Native, durable | 3,794 | 7,589 | Largest group commits, but 330 of 800 clients aborted |

Raw data: [results](../../../../benchmarks/spike/).

### Pool size

| Pool | Docker scripts/s | Native scripts/s (overload, aborts) |
|---|---|---|
| 32 | 1,857 | |
| 50 | | 1,029 |
| 64 | 3,160 | |
| 100 | 4,866-4,880 | 1,616, with aborts |
| 200 | | 2,782, with aborts |
| 400 | 3,655, `BufferContent` 75% | 3,794, with aborts |
| 800 | | 3,642, with aborts |
| No pooler, 800 connections | 2,013, aborted, `BufferContent` 88% | |

The pool protects Postgres. Too small starves it, and too large makes writers fight. The best size depends on flush speed: slower flushes need more waiting backends to share each one.

## Durable rate search

Native stack, real flushes, 60 s per rate. Verification passed in every row.

### Schema C, pool 200, the recommended setup

| Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Aborted clients | Primary cores | All cores | Top waits |
|---|---|---|---|---|---|---|---|---|---|
| 1,000 | 994 | 1,987 | 49 | 170 | 25 | 0 | 1.25 | 1.7 | `WALWrite` 48%, `SyncRep` 43% |
| **1,500** | **1,497** | **2,995** | **49** | **195** | **27** | **0** | **1.54** | **2.1** | `WALWrite` 48%, `SyncRep` 43% |
| 2,000 | 1,993 | 3,985 | 61 | 338 | 31 | 5 (0.004%) | 2.39 | 3.0 | `SyncRep` 42%, `WALWrite` 38%, `MultiXactGen` 10% |
| 2,500 | 2,367 | 4,735 | 3,017 | 6,597 | 35 | 4 | 3.19 | 3.9 | Fell behind |
| 3,000 | 2,512 | 5,025 | 5,428 | 19,009 | 34 | 0 | 3.75 | 4.6 | Saturated |
| 3,500 | 2,284 | 4,567 | 13,247 | 39,966 | 39 | 0 | 3.16 | 3.9 | Saturated |
| 4,000 | 2,826 | 5,652 | 15,102 | 35,099 | 31 | 8 | 4.21 | 5.2 | Saturated |

One run. See [`result.json`](../../../../benchmarks/spike/20261105-155517-macbook-limit-native-c-hash-all-pool200/result.json).

### Schema A, the original design, at the same rates

Four runs across pools 200 and 400. Aborted clients are in brackets.

| Target/s | p99 ms, each run | Aborted clients |
|---|---|---|
| 1,000 | 118, 118, 118, 166 | 26, 22, 0, 0 |
| 1,500 | 128, 134, 182, 209 | 37, 0, 31, 306 |
| 2,000 | 219, 204, 1,826, 23,770 | 308, 295, 166, 791 |
| 2,500 | 353, 20,114, 4,889 | 453, 687, 669 |

**Schema C removes nearly all aborts,** the main reason to choose it. Its p99 is inside schema A's range at 1,500/s, so a latency cost isn't established. It's one run, so it needs repeating.

**Two of these runs came from a mistake:** a script edit while running made a sequence repeat itself, though the extra runs are valid measurements. A later cleanup also deleted the just-started schema C pool 400 run, which had no recorded results.

## SLOs

### For this MacBook

| SLO | Target | Measured at 1,500/s | Basis |
|---|---|---|---|
| Durable database throughput | 3,000 commits/s | 2,995 | One run; not sustained through a checkpoint |
| Script p50 | ≤ 60 ms | 49 ms | About 2 commits |
| Script p99 | ≤ 250 ms | 195 ms | Allows for variation seen between runs |
| Aborted clients | ≤ 0.1% | 0 | The spec's error limit |
| Verification | 100% of runs | 100% | `verify.sql` |
| Commit durability | Not negotiable | `F_FULLFSYNC`, quorum | Never traded for speed |
| Full platform overhead, p99 | ≤ 300 ms at 650 payments/s | **Not measured** | Calculated; replace when built |

A stretch target of 4,000 commits/s has p99 338 ms and 0.004% aborts in one run. Don't adopt it before repeating that.

### The production SLO stays as designed

p50 ≤ 25 ms, p99 ≤ 100 ms and errors below 0.1% at 5,000 payments/s. **This Mac misses them at every durable rate,** because two commits on this SSD take about 45-50 ms on their own. With the fast VM flush, the database path did meet them at 1,000 scripts/s: p50 11 ms, p99 50 ms. So flush speed, not software, sets this floor.

## Bottlenecks

| # | Bottleneck | Kind | Evidence | Status | Next step |
|---|---|---|---|---|---|
| 1 | Docker port forwarding | Infrastructure | 2,018 to 3,319/s when removed | **Removed** | None; native avoids it |
| 2 | One PgBouncer process | Software | 0.99 core; 4 processes reached 4,880/s | **Removed** | Size per [production model](#production-capacity-assessment) |
| 3 | Pool size | Configuration | 3.7× from pool 50 to 400 | **Tuned** for this disk | Re-tune for each disk |
| 4 | Relation-extension lock on `payments` | Software, schema | Hundreds of lock timeouts; 0-8 with schema C | **Largely fixed,** one run | Repeat on Linux |
| 5 | Hot last pages (`BufferContent`) | Software, schema | Up to 88% in Docker; `update payments` and `update payment_attempts` lead | **Fix unmeasured** where it shows: schema C has no fast-flush run | Linux spike |
| 6 | Non-HOT updates: `state` is in partial-index predicates | Software, schema | Read from the schema; cost not measured | **Open** | Next iteration; could move timers to their own table |
| 7 | `MultiXactGen` waits, up to 10% | Software, unknown | New with schema C; cause not investigated | **Open** | Next iteration |
| 8 | Statement round trips | Software | Pipelining +24%, sale only, Docker, one run each | **Open,** not measured with real flushes | Next iteration, needs Npgsql batching |
| 9 | **SSD flush: 3.26 ms idle, 7.2 ms mean on the primary across the session** | **Hardware** | `WALWrite` + `SyncRep` 80-91%; three copies share one drive | **Saturated.** Can't fix here without weakening durability | Only different hardware helps |

**Not bottlenecks:** CPU, at most about 5 of 18 cores with schema C, and memory, with at least 57% free. Docker's limits and Postgres sizing gave no measured gain.

**Unresolved:** prepared statements aborted at both pools tried, so there's no conclusion. `commit_delay` 100 µs gave 3,061/s against 4,866/s in one run, with extra sampling on, so it wasn't adopted. The native "unsafe-fsync" run is mislabeled and must not be used: it changed only the primary, so its standbys kept flushing. It has been fixed in the script but not rerun.

## Realistic End-to-End Performance Expectations

**Labels:** **M** measured here · **X** extrapolated from a measurement · **A** assumption · **U** unverified claim.

### On this MacBook

A true end-to-end measurement doesn't exist, because the API, fraud service, PSP adapters, broker and workers aren't built. This is a calculation for the full platform sharing this Mac.

| Step | Value | Label |
|---|---|---|
| Clean durable commits/s | About 3,000 | M |
| Commits per payment, full mix (2 + 0.33 refunds + 1.17 callbacks) | 3.5 | Design formula |
| Database-limited rate | About 857 payments/s | X |
| Writes not in the spike (webhook state, projector, audit): −10-25% | **640-770 payments/s** | A |

| CPU per payment | ms | Label |
|---|---|---|
| Database and poolers: 0.69 cores per 1,000 commits × 3.5 | 2.4 | M on the sale mix, X on the full mix |
| Gateway, API, adapter, fraud, callbacks | 1.6-3.6 | A |
| Publisher, Kafka, webhooks, projector | 0.3-0.8 | A |
| Mock PSP and load generator | 0.6-1.6 | A |
| **Total, so about 4-6.5 of 18 cores at 770/s** | **5-8.5** | Mostly A |

CPU would still have room, so the SSD stays the limit, and app speed barely moves this result.

| Latency, excluding PSP wait | Database (M) | App and fraud (A) | Platform overhead |
|---|---|---|---|
| p50 | 49 ms | 10-20 ms | About 60-70 ms |
| p99 | 195 ms | 50-100 ms | About 250-300 ms |

| Scenario | Payments/s | Status |
|---|---|---|
| **One cell, planning figure** | **650-750** | Calculated |
| One cell, ceiling with errors and long tails | About 1,000-1,300 | Calculated, not sustainable |
| Two cells on the shared SSD | 650-1,400 | **Not measured,** script ready. A wide range, since each cell still flushes to one drive |
| 5,000 at the production SLO | **Not reachable on this Mac** | About 6× the clean commit rate |

### Production capacity assessment

#### Load to carry

| Item | Value | Label |
|---|---|---|
| Commits/s at 5,000 payments/s (10,000 + 1,666 + 5,833) | 17,499 | Design formula |
| Requests/s: sales, refunds and callbacks | 11,666, plus reads | Design formula, reads unknown |
| Peak or average | Unknown ([D36](../../../decisions.md#d36)) | U, changes storage most |

#### What the Mac tells us

| Input | Value | Label | How production uses it |
|---|---|---|---|
| Commits/s with fast flushes, pool 100 | About 9,750, twice, on one laptop VM | M, not durable here | What software allows when the disk is fast |
| Software at fast flush, 1,000 scripts/s | p50 11 ms, p99 50 ms | M, not durable here | The database path alone fits the production SLO once flushes are fast. [The whole platform is tighter](../../../architecture/prd-deployment.md#latency) |
| Primary CPU | 0.51-0.86 M5 cores per 1,000 commits, rising with load | M | Primary sizing |
| WAL | 2.0-5.4 KB per commit | M, sale mix | Disk and replication bandwidth |
| Database size | 2.03 KB per payment | M, mostly prefill | Storage growth |
| PgBouncer | 1 core per about 6,600 commits/s | M, in VM | Pooler count |
| Connection hold | About 10.3 ms per transaction (100 ÷ 9,733) | X | Pool size, by Little's law |
| Standby CPU | About 0.3 core per standby at 9,750 commits/s | M | Replay isn't the limit |
| Correctness at overload | Verification passed in every run | M, spike SQL only | Overload doesn't break these money rules |
| Overload response | Timeouts fail fast, and the pool protects Postgres | M | Supports shedding with 429 and 503 |

#### Assumptions

| Assumption | Used for | If wrong |
|---|---|---|
| Production NVMe flushes in 50-200 µs, against 60 µs in the VM and 3,260 µs on this SSD | Everything: lets fast-flush results apply | **Biggest risk.** Must be measured with `pg_test_fsync` |
| Zone-to-zone delay of 1-2 ms adds to each commit | Hold time 12 ms | More connections or lower per-cell rate |
| 1 M5 core ≈ 1.5-3 cloud vCPUs | All CPU sizing | Scale vCPUs |
| API work is 1.6-3.6 ms per payment | API pods | Changes pods, not the database limit |
| 10-25% more writes than the spike | Headroom | Lowers the per-cell rate |
| Cells scale almost linearly, since they share only the edge | Total capacity | **Not tested:** the two-cell run was skipped, and shares one SSD anyway |

#### Can one primary do it?

**Not shown.** 17,499 commits/s is 1.8× the best measured 9,750, on a machine also running the generator and poolers. Pipelining's measured +24% would reach about 12,100, and schema C's effect at fast flush is unmeasured. A larger server might close the gap, but contention rose as concurrency did, so scaling won't be linear. Connections needed would be about 210 busy (17,499 × 12 ms), or 350 at 60%, near where pool 400 got worse on the old schema. **This stays G1's question, on Linux.**

#### With cells, the design's fallback

Per [Architecture](../../../architecture.md#cells-and-routing), 4 active cells plus 1 spare, at 1,250 payments/s each:

| Per cell | Value | Label |
|---|---|---|
| Commits/s | 4,375, up to 7,000 at 2,000 payments/s | X |
| Share of measured 9,750 commits/s | **45%, up to 72%** | X, on the fast-flush assumption |
| Primary CPU | 3.8 M5 cores, so 10-19 vCPUs at 60% | X and A |
| WAL | 9-24 MB/s, and 18-48 MB/s to replicate | X |
| Busy server connections | About 53 (4,375 × 12 ms), so pool 100 runs about half full | X and A |
| Storage at this rate all day | About 219 GB/day per cell, 877 GB/day total | X, depends on D36 and retention |

**This is the strongest result here:** one cell needs less than the software has already done on a laptop, provided production's disk flushes fast and durably.

#### Expected Kubernetes topology

[Production deployment](../../../architecture/prd-deployment.md) has the topology, replica counts, CPU, memory, connection budget and failure behavior. In short: **about 190 vCPU and 600 GB per cell, and about 1,000 vCPU and 3.1 TB in total,** ±50%.

### Conclusion

**Is 5,000 payments/s plausible? Yes, with cells, but it hasn't been demonstrated.**

**Supporting evidence:**

1. Each limit found other than the disk was removed or has a known next step.
2. The final limit is this SSD's flush, and production uses a different class of disk.
3. When flushes are fast, the software reached 9,750 commits/s on one laptop VM, more than twice what one cell needs at 1,250 payments/s.
4. At fast flush, the database path met the production latency targets at 1,000 scripts/s.
5. Money rules held in every run, even at overload.

**What it requires:** about 1,000 vCPUs across 5 cells, NVMe with fast durable flushes in 3 zones, schema C or similar, and 4 or so poolers per cell.

**What would change this conclusion:**

- Production disks flush much slower than assumed
- Zone delay costs more than assumed
- Cells don't scale close to linearly
- The real platform writes far more per payment

### Still needs production validation

| # | Validate | How | Blocks |
|---|---|---|---|
| 1 | Durable flush time on the real disks | `pg_test_fsync` | Everything below |
| 2 | Commits/s at real flush and zone delay | Linux spike, 100M rows, separate generator | G1 |
| 3 | Full-mix capacity | V13 on Linux | G1, G6 |
| 4 | One cell at 1,250-2,000 payments/s through the real API | L1 and L5, after Tasks 7-17 | Stage 4 |
| 5 | Scaling across cells | Several cells on their own disks | Stage 4 |
| 6 | Sustained behavior through checkpoints and vacuum | 60 min run | L5 |
| 7 | API CPU per payment and real unmodeled writes | Platform benchmark | Sizing |
| 8 | Failover under load | C-series tests | Production |

## Not measured

Skipped by the owner on 2026-11-05, in favor of benchmarking the full platform once it's built:

- Full-mix capacity (V13). Only a short smoke test ran, so 3.5 commits per payment remains a formula
- Two cells
- The other variants: V1-V12, including row-level security, schema B, the outbox options and callback batching
- Any run longer than 60 s, so sustained behavior through a checkpoint isn't established
- Real flushes combined with pipelining
- Flush sensitivity on all three nodes
- Whether schema C helps at fast flush

## Decisions

The owner approved both on 2026-11-05.

- **[D25](../../../decisions.md#d25), partitioning:** schema C, with hash partitions for `payments`, `payment_attempts`, `ledger_transactions` and the outbox. `attempt_id` and `source_event_id` aren't unique alone, and most data retires by batched delete rather than by detaching time-based partitions. How long data is kept stays open.
- **[D26](../../../decisions.md#d26), G1:** remains **open**. This Mac can't answer it. The build targets one cell, and G1 closes on Linux before Stage 4.
