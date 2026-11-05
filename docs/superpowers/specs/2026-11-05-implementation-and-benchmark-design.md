# Implementation and Benchmark Design

What this build effort delivers, what counts as done, and the rules that make a benchmark result trustworthy.

Status: Accepted by the owner · Last updated: 2026-11-05

The system itself is defined in the [design docs](../../../README.md#documents), and this spec doesn't restate it. Where a value already has a home in those docs, this spec links to it. The [implementation plan](../plans/2026-11-05-open-payments-implementation-and-benchmark.md) holds the tasks.

## Goal

Build Open Payments and find out, by measurement, whether it sustains [5,000 completed payments/s](../../architecture.md#goals-and-slos) with no duplicate charges, no lost records and balanced books.

**A measured "no" is a valid outcome.** The effort succeeds if it establishes the real limit, what sets it and how the system fails there. Gates are never relaxed to reach the target ([playbook](../../scaling-playbook.md#measures-and-gates)).

## Scope

**In scope:** the `Committed` rows of the [scope table](../../architecture.md#scope-and-status), built through [phases 0-5](../../verification.md#build-phases), and the [ramp plan](../../scaling-playbook.md#ramp-plan) with its load and failure tests.

**Out of scope:** `Proposed` states and everything listed under [Assumptions and limits](../../decisions.md#assumptions-and-limits), including payouts, disputes, multi-region and the formal PCI assessment. Merchant profiles are limited to a minimal `api_keys` table. Cells are built only if G1 requires them ([D14](../../decisions.md#d14)).

## Definition of done

| Level | Done when |
|---|---|
| First result | The spike has answered G1 on Linux, and steps 0-5 are recorded as preview runs, with capacity per instance measured |
| Target claim | L5 passes on the complete payment path (fraud, vault and edge included) on Linux, with all applicable checks passing |
| Sign-off | The [sign-off rule](../../verification.md#build-phases) is met, and the [final report](../../scaling-playbook.md#final-report) is written |

A pass against the mock PSP is not production capacity. The target claim always carries the result of G2, because the PSP contract may set a lower limit.

## Build sequence

Each stage produces something measured before the next begins.

| Order | Stage | Why here |
|---|---|---|
| 1 | Design closure | The accounts and data-lifecycle choices must precede the first migration ([Stripe research](../../stripe-research.md#build-order)) |
| 2 | Database spike | Answers whether one primary is enough. A "no" changes the architecture, so it comes before the code |
| 3 | Test tools | Correctness checks must exist before the code they judge |
| 4 | Payment core, then first ramp steps | Proves correctness at low load, and gives the first real capacity number |
| 5 | PSP and recovery, then async, hardening, security | Each is planned in detail only once earlier numbers exist |

## Design changes this effort needs

D21-D24 are accepted and recorded in [Decisions](../../decisions.md). D25 and D26 follow the spike.

| Record | Change | Reason |
|---|---|---|
| D21 | Accounts and fund flows, with `clearing` or `terminal` and `real` or `logical` classes; `effective_at` and `subject_id`; minimal `api_keys` and `currencies` tables | These tables are named but unspecified, and block the first migration. Fee treatment stays `Open` |
| D22 | Split the payment-transition module into a pure `Decide` function and a thin database shell | Allows exhaustive state tests and golden datasets. Amends [D2](../../decisions.md#d2) |
| D23 | Add checks CK7-CK10 and one canonical log line per request | A balanced ledger can still be wrong. The log line also measures platform overhead directly |
| D24 | Correct the derived load and the phase gates, as below | The current gates can't be met as written |
| D25 | Partitioning and retention, chosen from spike results | `payment_attempts` can't be partitioned as specified without changing its key |
| D26 | The answer to G1 | Decides one cell or several |

**D24 in detail:**

- **Derived load:** count the real two-transaction flow. Our estimate is about 20-22 row writes and up to 4 commits per auto-captured payment, against the documented 8-10 writes. The spike replaces this estimate.
- **C31 split by phase:** C31a in Phase 1 (E1-E9, E11-E13, E16-E18, E21), C31b in Phase 2 (E10, E19, E20, E22-E24), C31c in Phase 3 (E14, E15).
- **Full path before the final number:** minimal synchronous fraud, vault and edge gateway join Phase 4, and L5 runs again after Phase 5.

## Benchmark rules

### What a run measures

Admitted req/s, rejected req/s and completed payments/s are recorded separately, as the [standard names](../../glossary.md#standard-names) define them. The target applies to **platform overhead**, measured on the server as total time minus PSP wait, because a load generator can't see it. End-to-end latency is recorded against a fixed PSP latency.

### When a result can be trusted

A run is one of three things:

| Result | Meaning | Counts as proof? |
|---|---|---|
| **Invalid** | The measuring tools interfered or disagreed | No, and it says nothing about the system |
| **Preview** | Valid, but from before Phase 3, or from a laptop | No. Useful for comparing runs and finding bottlenecks |
| **Recorded** | Valid, on Linux, from Phase 3 onward, with all checks applicable | Yes, once it meets the gates |

A run is **invalid** if any of these is true:

- The generator dropped arrivals, or exceeded 80% CPU.
- The mock PSP added more than 5 ms at p99.
- The completed-payments metric differs from the database count.
- The git tree was dirty, or the configuration wasn't recorded.

A valid run **fails its gates** on any [gate miss](../../scaling-playbook.md#measures-and-gates), including any failed correctness check. A fast run with wrong money is a failed run.

### Which checks apply

A check that can't apply yet is reported as not applicable, never as passed. Every check must fail on injected corruption and on zero rows, or it isn't evidence.

| Stage | CK1-CK3 | CK4 | CK5 | CK6 | CK7-CK10 |
|---|---|---|---|---|---|
| Spike | CK1 and CK3 by SQL | n/a | n/a | n/a | n/a |
| After Phase 1 | Yes | Every transition has its outbox row | Yes, with no PSP timeouts injected | Without outbox age | Completeness, cross-check |
| After Phase 2 | Yes | Same as Phase 1 | Yes, with timeouts | Without outbox age | Adds clearing, timeliness |
| Phase 3 onward | Yes | Full | Yes | Full | Yes |

### Test conditions

- **Arrival model:** open, at a fixed rate, so a slow system can't reduce its own load.
- **Workload:** the [example mix](../../scaling-playbook.md#workload-and-load-generation), replaced by a real one when known. Merchant traffic is skewed 80/20, since uniform traffic hides hot rows.
- **Data:** prefilled before measuring, starting at 100M payments on Linux, with the size recorded. An empty database flatters every number.
- **Environment:** Linux with NVMe, WAL on its own volume, quorum replication across zones, and generators on separate machines. Exact machine sizes are recorded per run.
- **PSP:** fixed latency for comparable runs, with separate runs for variable latency and failures. The mock PSP must be shown to carry at least 25,000 req/s on the same class of hardware.
- **Results:** each run keeps its configuration, commit, summary and check output in `benchmarks/`, using the [results log](../../scaling-playbook.md#results-log).

### What the spike must answer

The spike uses the real transaction shape on the revised schema, and reports sustained commits/s at 60% primary CPU or less against the rate the mix requires.

| Question | Answers |
|---|---|
| What does quorum commit cost? | G3, provisional |
| What does the balance trigger cost? | G4, provisional |
| What does row-level security cost? | Tenant backstop |
| Does the connection pool or PgBouncer limit first? | Connection budget |
| Which partitioning scheme holds up? | D25 |
| Does pipelining statements help? | Transaction design |
| Can idempotency keys expire by batched delete under load? | [D19](../../decisions.md#d19) |
| How many refunds/s can one merchant sustain under the advisory lock? | [D9](../../decisions.md#d9) |

## Stop conditions

Stop and bring the evidence to the owner when:

- G1 says one primary isn't enough. Cells change the roadmap.
- G2 says the PSP can't reach the target. More engineering won't fix a contract limit ([Assumptions](../../decisions.md#assumptions-and-limits)).
- Meeting a gate would need asynchronous replication, which gives up RPO 0 and is an owner decision.
- Any run shows a duplicate charge, lost record or imbalance that can't be explained. Correctness work comes before all further load work.

## Owner decisions

| Decision | Needed by | Recommendation |
|---|---|---|
| Where the recorded benchmarks run | The spike's Linux run | Rent Linux machines by the hour, only during runs |
| Which PSP sandbox to use | Stage 2 | Choose before Stage 2. It must support idempotency keys, inquiry by our ID and 3D Secure |

Settled on 2026-11-05: D21-D24 are accepted. Auto-capture uses a request field, the recommended default, since the contract is still a draft.

## Open items

These don't block the first result, and stay `Open` until their stage:

- The [fraud fallback policy and shed order](../../decisions.md#open-questions) need a business owner before Stage 4.
- The test-mode isolation mechanism is decided in Stage 5 ([D20](../../decisions.md#d20)).
- Thin or full merchant events is decided in the event catalog.
- Machine sizes and costs are unknown until the environment is chosen.
