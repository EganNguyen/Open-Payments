# Operations Dashboard Plan

The console's home screen: throughput, correctness, reliability and payment-lifecycle visibility, with the work queue one click away.

Status: **Approved by the owner and built** · Last updated: 2026-11-09

> **For whoever executes this:** This is owner-directed, in chat on 2026-11-09. It changes the console spec's navigation: the dashboard is `/`, and Needs attention moves to `/attention`. The [Needs attention plan](2026-11-09-needs-attention-ux.md) still owns the queue, its wording and its role rules. The console plan's [Global Constraints](2026-11-06-console.md#global-constraints) apply: read-only, no reference to `OpenPayments.Transitions`, and no new write path.

## Purpose

**Done means:** a staff member opening the console sees whether the platform is moving money, whether the books agree, whether it is healthy, and where payments are in their lifecycle. If a person needs to act, the page says so at the top and links to the full queue.

This is an operations view, not revenue analytics. It reports only what the platform already measures; it adds no chart history and no financial reporting.

## Layout

From top to bottom:

1. **Title and freshness.** "Dashboard", then "Checked 14:02:31 UTC" with a Refresh button, and the existing `_DataAsOf` banner when the replica is behind.
2. **Needs attention strip.** The queues with rows, each with its count, linking to `/attention#q-<queue>`. When nothing waits, "Nothing needs attention." with the check time.
3. **Four cards**, two per row on desktop and one per row on iPhone:
   - **Throughput:** completed payments/s, admitted requests/s, rejected requests/s, platform overhead p50/p95/p99, PSP wait p99, in-flight.
   - **Reliability:** replica lag, operations waiting over 60 s, unresolved too long, database deadlocks/s, PgBouncer max wait, oldest outbox age.
   - **Payment lifecycle:** payments created in the last 15 minutes by current state, with the oldest age in each state; authorized now; refunds pending.
   - **Correctness:** live invariant checks for refund totals, capture records and capture amounts, each Pass or its issue count.

Each card degrades on its own. A missing Prometheus series shows "—", never zero. A timed-out checker says so; it doesn't take the page down.

## Data sources

| Source | What it supplies | How it is read |
|---|---|---|
| Prometheus | Throughput and runtime health | `Console:Metrics:PrometheusUrl`, 2 s timeout, 5-minute rate window |
| Replica | Lifecycle counts and summary | `payments_merchant_created` for the bounded 15-minute window; partial indexes for pending and authorized counts |
| Replica | Correctness invariants | The CK3, CK9 and CK10 shapes, filtered to the deployment's merchants, plus the CK5 stuck count |
| `QueueReader` | Attention strip and unresolved count | The same read as `/attention` |

Correctness checks deliberately exclude `psp_events`, which the console's read role can't see. CK1-CK10 remain the full `op-checks` run; the dashboard is a live subset, not a replacement.

## Configuration

| Setting | Default | Meaning |
|---|---|---|
| `Console:Metrics:PrometheusUrl` | empty | Empty or unreachable disables the Throughput card; other cards still work |
| `Console:Metrics:Timeout` | 2 s | Limit on the Prometheus calls |
| `Console:Metrics:Window` | 5 min | PromQL rate window |
| `Console:Dashboard:LifecycleWindow` | 15 min | How far back the lifecycle card looks |
| `Console:Dashboard:StuckAfter` | 60 s | When an unresolved operation counts as stuck |

The local compose console sets `Console__Metrics__PrometheusUrl: http://prometheus:9090`.

## Tasks

### D1: Metrics reader

- [x] `PrometheusOperationsMetrics` reads the throughput and reliability series, with a missing series as null and a failed query as unavailable.
- [x] Tests: scalar and labelled parsing, largest rejection reason, empty URL, failed queries, missing series.

### D2: Lifecycle and correctness reader

- [x] `DashboardReader` returns the bounded lifecycle snapshot, summary counts and three live invariant checks, with a timeout local to each section.
- [x] Tests: recent vs older payments, issue counts, no data, attention strip.

### D3: Dashboard page and navigation

- [x] `/` serves the dashboard; `/attention` serves the queue.
- [x] Sidebar becomes Dashboard, Needs attention, Payments, Audit log, Staff; tabs become Dashboard, Attention, Payments, More.
- [x] The badge stays on Needs attention and the Attention tab.

### D4: Layout and accessibility

- [x] Desktop two-column cards and one-column cards at 390 px.
- [x] Browser tests: axe in light and dark at 1280 and 390 px, no horizontal scroll, 44 px targets.
- [x] The existing empty state at `/attention` still shows only "Nothing needs attention." with the check time.

## Build record

| Item | Result |
|---|---|
| Console tests | `dotnet test tests/OpenPayments.Console.Tests`: 166 passed, 0 failed |
| Browser tests | `dotnet test tests/OpenPayments.Console.Browser.Tests`: 46 passed, 0 failed |
| Build | `dotnet build OpenPayments.slnx -warnaserror`: 0 warnings |
| Whole suite | `dotnet test OpenPayments.slnx`: 452 passed, 0 failed |
| Live stack | The local console image is rebuilt; Prometheus is configured at `http://prometheus:9090` |

## Limits

- `op_payments_by_state` and `op_outbox_oldest_age_seconds` are planned but not emitted yet, so those panels show "—" until Stage 2 emits them.
- The lifecycle card is a bounded recent window, not a historical series.
- The correctness checks can time out at large data volumes; the card says so rather than showing a false pass.
- The dashboard reads the replica. It is not on the payment path, and it writes nothing.
