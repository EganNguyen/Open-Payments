# Implementation and Benchmark Design

What this build delivers, what a design review found wrong with the plan to build it, what is ready, and what counts as done.

Status: Reviewed; core decisions accepted · Last updated: 2026-11-05

The system is defined in the [design docs](../../../README.md#documents), and this spec doesn't restate them. The [implementation plan](../plans/2026-11-05-open-payments-implementation-and-benchmark.md) holds the tasks.

**The verdict:** the owner has accepted D35, D37, D38 and D39, so the core design is `READY` to build. The first migration waits on the spike, and the spike's real answers wait on a choice of Linux machines. Throughput claims stay `MEASURE-FIRST`. Nothing has been built or measured, so every number is an estimate or a target.

## Goal

Build Open Payments and find out, by measurement, whether it sustains [5,000 completed payments/s](../../architecture.md#goals-and-slos) with no duplicate charges, no lost records and balanced books.

**A measured "no" is a valid outcome.** The effort succeeds if it establishes the real limit, what sets it and how the system fails there. Gates are never relaxed to reach the target.

## Scope

**In scope:** the `Committed` rows of the [scope table](../../architecture.md#scope-and-status), built through [phases 0-5](../../verification.md#build-phases), and the [ramp plan](../../scaling-playbook.md#ramp-plan) with its load and failure tests.

**Out of scope:** `Proposed` states and everything under [Assumptions and limits](../../decisions.md#assumptions-and-limits), including payouts, disputes, multi-region and the formal PCI assessment. Cells are built only if G1 requires them.

## What the review found

The design is strong on one question: did our own write succeed safely? Its weak spots are where it meets things it doesn't control, and where its numbers were never derived from its own flow.

### Money could be wrong

| # | Finding | Why it matters | Resolution |
|---|---|---|---|
| 1 | "PSP says not found, so the attempt failed" isn't safe | A delayed request can land after the lookup. The customer then pays on a new attempt and is charged twice. This was the design's main crash-recovery path | [D27](../../decisions.md#d27): only proof fails an attempt |
| 2 | Inquiry by our ID is assumed, but real PSPs don't work that way | [Stripe](https://docs.stripe.com/error-low-level) recovers by replaying the same key, and warns against any retry after 24 hours. [Its Search](https://docs.stripe.com/search) can lag an hour in outages. [Adyen](https://docs.adyen.com/api-explorer/Checkout/latest/post/payments/_paymentPspReference_/cancels) offers cancel by our reference | [D27](../../decisions.md#d27): each adapter declares its recovery mode |
| 3 | A replay after the PSP's key retention can run as a new charge | The docs treated the same key as safe forever. Stripe's retention is [at least 24 hours](https://docs.stripe.com/api/idempotent_requests), and Adyen's [7 days or more](https://docs.adyen.com/development-resources/api-idempotency) | [D27](../../decisions.md#d27), [D34](../../decisions.md#d34): timers ordered, replay window bounded |
| 4 | Cancelling during `COMMIT` can silently break RPO 0 | [Observed on 18.6](../../../benchmarks/spike/commit-cancel.md): a cancel returns a normal `COMMIT` success with a warning, while no standby has the row. `statement_timeout` doesn't limit the wait either. The guide's own sample code passed a cancellable token | [D28](../../decisions.md#d28), test C43 |
| 5 | A callback could be marked seen, then lost in a crash | Inserting into `psp_events` and applying were separate steps, and dedupe would then drop the retry | [D30](../../decisions.md#d30) |
| 6 | An `AUTHORIZED` payment has no way out but capture | A merchant can't release a customer's funds. `auth_hold` never clears, so CK7 would page on every uncaptured payment. Late approvals need a person | [D37](../../decisions.md#d37), accepted |
| 7 | Replays answered with a stale result, and refusals were undefined | A stored 202 goes stale once the operation resolves | [D31](../../decisions.md#d31) |
| 8 | Event `sequence` had no source | Using `version` leaves gaps, because a refund reservation bumps it with no event, and consumers park on gaps | [D32](../../decisions.md#d32) |

### Capacity numbers were wrong or missing

| # | Finding | Why it matters | Resolution |
|---|---|---|---|
| 9 | Commit load was understated, then overstated | The docs said 8-10 writes. Our first correction said up to 4 commits for auto-capture, which ignored that PSPs can do it in one call | [D29](../../decisions.md#d29): about 12k commits/s with one call, about 22k with two |
| 10 | PSP callbacks weren't counted | Up to one per operation adds about 6-11k requests and commits per second | [D30](../../decisions.md#d30), spike variant V10 |
| 11 | Outbox publishing wasn't counted | Marking each row published adds an update per event on the primary | `MEASURE-FIRST`, spike variant V9 |
| 12 | The PSP may set the limit long before our code | Stripe's default is [100 requests/s per account](https://docs.stripe.com/rate-limits). G2 was scheduled late, and its answer depends on who owns the account | [D35](../../decisions.md#d35), accepted: limits are per merchant account. Ask before Phase 2 |
| 13 | The target's shape is undefined | A full day at 5,000/s is 432 million payments, about 61% of [Visa's fiscal 2025 daily average](https://s29.q4cdn.com/385744025/files/doc_downloads/2025/Visa-Fiscal-2025-Annual-Report.pdf) (257.5 billion a year). Data volume may limit before throughput | [D36](../../decisions.md#d36), owner; gate G6 |
| 14 | One in-flight cap covers different kinds of waiting | It can't tell requests waiting on a PSP from a stuck database, and mass `UNKNOWN` recovery would compete with live traffic for PSP quota | [D34](../../decisions.md#d34) |

### Design that may not earn its cost

| # | Finding | Why it matters | Resolution |
|---|---|---|---|
| 15 | The refund lock guards nothing in v1 | With no payouts or retained fees, per-payment limits already stop a negative balance. The lock may cap a merchant at a few hundred refunds/s | [D38](../../decisions.md#d38), accepted: deferred |
| 16 | Our own vault brings the heaviest compliance scope and a shared hot-path dependency | [Hosted fields can keep merchants in the lightest assessment](https://www.securitymetrics.com/blog/ultimate-cheat-sheet-making-online-pci-compliance-work-you). The trade-off is that PSP tokens don't move between PSPs | [D39](../../decisions.md#d39), accepted: vault deferred |
| 17 | Fraud ran before the idempotency check, and a block had no outcome | Retries inflated velocity counters, and a block left no row, event or answer | [D33](../../decisions.md#d33) |
| 18 | Two idempotency stores | `refunds` kept its own key and unique index, adding writes and rules for no benefit | [D31](../../decisions.md#d31) |

### Requirements nobody had stated

| # | Finding | Resolution |
|---|---|---|
| 19 | Whether the platform holds merchants' money. The ledger, licensing, credentials and PSP limits all depend on it | [D35](../../decisions.md#d35), accepted: it doesn't |
| 20 | How a customer's browser calls the API. The docs let customers start attempts, but the API has only secret merchant keys | [D39](../../decisions.md#d39), and [open questions](#unresolved-questions) |
| 21 | Whether v1 carries real money. With real cards, disputes happen whether or not they are designed | [Open questions](#unresolved-questions) |

### What held up

These survived challenge, and the review keeps them: writing intent before the PSP call, state with ledger and outbox in one transaction, conditional updates, one-live-attempt unique indexes, `UNKNOWN` as a real state, quorum synchronous replication, one cell first, and shedding overload instead of queueing it.

## Decisions

**`Committed` by this review,** because they fix correctness or define what was ambiguous, and change no scope:

| Record | Decision |
|---|---|
| [D27](../../decisions.md#d27) | A negative PSP answer needs proof; adapters declare recovery modes |
| [D28](../../decisions.md#d28) | `COMMIT` is never cancelled; a failed commit is unknown |
| [D29](../../decisions.md#d29) | Auto-capture is one PSP call where supported |
| [D30](../../decisions.md#d30) | Callbacks apply atomically, and count as load |
| [D31](../../decisions.md#d31) | Idempotency answers defined for every case; one key store |
| [D32](../../decisions.md#d32) | Deterministic event and ledger identity; balance per currency |
| [D33](../../decisions.md#d33) | Key check before fraud; fraud blocks are recorded outcomes |
| [D34](../../decisions.md#d34) | Separate limits per kind of waiting; fixed timer order |

**`Committed` by the owner on 2026-11-05,** each changing scope:

| Record | Decision | What it adds |
|---|---|---|
| [D35](../../decisions.md#d35) | Merchants connect their own PSP accounts; v1 never holds funds | `psp_accounts`, limits and callback secrets per account, C51 |
| [D37](../../decisions.md#d37) | Void and authorization expiry in v1 | Two states, a void attempt, `authorized_until`, automatic void of late approvals, C50 |
| [D38](../../decisions.md#d38) | Defer the per-account refund lock | Removes a lock; C35 returns with fees or payouts |
| [D39](../../decisions.md#d39) | PSP-hosted card fields; our own vault deferred | Eight deployables; client-side access to design; an assessor must confirm scope before live cards |

D25 and D26 stay reserved for the spike's answers.

## Owner decisions

These remain open. Each has a recommendation.

| Decision | Recommendation | Blocks | If unanswered |
|---|---|---|---|
| [D36](../../decisions.md#d36): is 5,000/s a peak or an average? | Peak, with a stated average | L2, G6 | L2 stays as a full day at peak |
| Where do recorded benchmarks run? | Rent Linux machines by the hour | Task 6's answers | No G1 answer, so no first migration |
| Which PSP sandbox, and one PSP or two in v1? | One to start; choose by its recovery mode, sale support and limit | Stage 2 | C39 can't run, so the API can't freeze |

## Design readiness

`READY` means its important questions are resolved. `MEASURE-FIRST` means a number decides the design. `NEEDS-OWNER` means a decision does. `DESIGN-NEEDED` means it is in scope but not designed. None of these says the thing works.

| Area | Verdict | What's left | Settled by |
|---|---|---|---|
| Crash recovery order (intent, PSP, result) | `READY` | Nothing | C3, C4, C15 |
| State machine, including void and expiry | `READY` | Authorization window per PSP | U2, C50 |
| Pure `Decide` and shell | `READY` | Nothing | U2, U3 |
| Idempotency | `READY` | Key expiry method | E1-E4, E25-E29, C5, C41; V7 |
| Commit semantics | `READY` | Server behavior [observed](../../../benchmarks/spike/commit-cancel.md); Npgsql's timeout behavior to confirm | Task 13, C43 |
| PSP recovery contract | `READY` | Each adapter's mode, once a PSP is chosen | C44, C45, U5 |
| Merchant PSP accounts | `READY` | Onboarding flow, with the merchant platform | C51, U6 |
| PSP adapter for a real provider | `NEEDS-OWNER` | Which PSP | Then C39 |
| PSP rate limit against target (G2) | `NEEDS-OWNER` | Which PSP, then its limit per account | G2 |
| Callbacks | `READY` | `psp_events` retention, in D25 | C9, C46, C48 |
| Reconciler and timers | `READY` | Values | C26, C47 |
| Ledger mechanics | `READY` | Nothing | U3, CK1 |
| Fund flows and account meaning | `READY` | Fee treatment stays `Open` | CK7-CK10 |
| Balance trigger cost (G4) | `MEASURE-FIRST` | Trigger against audit query | V2, V12 |
| Refund limits per payment | `READY` | Nothing | C6, CK3 |
| Refund balance lock | `READY` | Deferred; restore before fees or payouts | [D38](../../decisions.md#d38), then C35 |
| One primary's capacity (G1, G3) | `MEASURE-FIRST` | The real commit rate | Spike, then L1 |
| Connection pool and PgBouncer | `MEASURE-FIRST` | Size and process count | V4 |
| Partitioning and retention (D25, G6) | `MEASURE-FIRST` | Scheme, plus data volume | V5, V7, [D36](../../decisions.md#d36) |
| Outbox publishing | `MEASURE-FIRST` | Marking rows, marker or log capture | V9 |
| Event ordering and consumers | `READY` | Nothing | C17 |
| Webhooks | `READY` | Event shape confirmation | C27, U7 |
| Load control | `READY` | Values | L3, C30, C48, C49 |
| Fraud rules | `READY` | Fallback policy needs an owner | C14, F1 |
| Card data | `READY` | An assessor confirms scope before live cards | U8, O4 |
| Client-side API access | `DESIGN-NEEDED` | Client secret format, scopes and lifetime | Stage 4 |
| Test mode isolation | `DESIGN-NEEDED` | Mechanism is `Open` | Stage 5 |
| Cells | `MEASURE-FIRST` | Only if G1 fails | G1 |
| API contract | `MEASURE-FIRST` | Frozen only after a real 3D Secure flow | C39 |
| Kubernetes and HA | `READY` | Values | C7, C10, C11, C19, C20 |

## Delivery

Each stage passes three [gates](../../delivery-workflow.md): design by Opus, build by DeepSeek, and independent review by Opus in a fresh session. Stage 1's design gate holds orders 1-2 below, and is [recorded here](../../gates/stage-1-design.md). Its build gate holds orders 3-5, followed by its review gate. Orders 6-9 are stages 2-5, each with all three gates.

## Build order

| Order | Work | Needs | Produces | Why here |
|---|---|---|---|---|
| Done | Design closure and this review | | Decisions, contract draft | |
| 1 | Database stack and spike scripts, tasks 3-5 | Docker | Runnable workloads | Needs no owner decision |
| 2 | Spike on Linux, task 6 | Machines, order 1 | G1, G3, G4 provisional; D25, D26; first migration | A "no" changes the architecture |
| 3 | Test tools, tasks 7-11 | First migration | Mock PSP, checks, harness | The tools that judge the code exist first |
| 4 | Payment core, tasks 12-16 | Order 3 | Phase 1 exit | The rules settle before the layers that use them |
| 5 | First ramp steps, task 17 | Order 4, Linux | Capacity per instance | First full-system number |
| 6 | PSP and recovery | PSP choice, G2 answer | C39, API frozen | Real providers before more scale |
| 7 | Async workers | Publisher choice from V9 | First recorded runs | CK4 and CK6 apply from here |
| 8 | Hardening with the full synchronous path | Rented cluster, D36 | L1-L6, G1-G6 | |
| 9 | Security and operations, then L5 again | Assessor before live cards | The sign-off number | |

The build doesn't start until the design gate closes, so it waits on the Linux spike. Starting early on a provisional migration would need the owner's explicit approval, accepting a rework of `0001_initial.sql` once D25 is decided.

## Acceptance criteria

Each is checked by a named test with a recorded run. **Exact values are from the docs, and are starting points where those say so.**

### Money

| Criterion | Passes when | Test |
|---|---|---|
| No duplicate effect | For every PSP key, the mock's effect log has one effect, every effect has a non-failed attempt, and every `SUCCEEDED` attempt has an effect | CK2, after every run |
| Same key, 100 concurrent | One payment, one effect, identical answers | C5 |
| Late landing | With a not-found then a late approval: attempt not failed on the lookup, 0 extra charges | C44 |
| Key retention | Past retention, 0 replays sent | C45 |
| Ambiguous commit | Across forced faults on A and B: 0 cancel requests sent during `COMMIT`, 0 PSP calls after an unknown A, 0 declines after an unknown B, 0 answers of success without a standby copy, and the wait stops at its limit | C43 |
| Client disconnect | Disconnect after A: the attempt reaches a final state, and a retry returns it | E3 |
| Refund limit | 50 concurrent refunds above captured: `refunded + pending` never above captured | C6, CK3 |
| Ledger | Every `txn_id` balances per currency, and 0 rows changed or deleted | CK1, U3 |
| Completeness | Every `CAPTURED` payment has its capture transaction, and `captured_minor` equals the ledger sum | CK9, CK10 |
| Clearing | No clearing balance past its age limit, with `auth_hold` zero in every final state | CK7 |
| Void against capture | Race: one PSP effect. Timeout: no state change. Capture after `VOIDED` or `EXPIRED`: rejected | C50 |
| Late approval | Attempt in `REVIEW`, no ledger entry, voided where allowed, and that void doesn't block a capture | C38 |
| Check validity | Every check fails on injected corruption and on zero rows | Task 9 tests |

### State and recovery

| Criterion | Passes when | Test |
|---|---|---|
| Transitions | Every state × command is accepted only if listed, and all invariants hold at every step of a bounded search | U2 |
| Callback order | All permutations of duplicate, delayed and reordered callbacks reach one final state | C9 |
| Callback crash | 0 events lost; early events park and then apply | C46 |
| Unresolved work | Nothing past the reconciler threshold after the run ends | CK5 |
| Recovery isolation | With thousands `UNKNOWN`: recovery within its quota, live traffic keeps its share | C47 |
| Merchant isolation | One revoked PSP credential: that merchant fails fast with no `UNKNOWN`, the PSP's breaker stays closed, others keep their SLO | C51 |
| Deadlocks | Forced lock collisions: `40P01` is counted, retried at most twice; `55P03` gives 503 | C34 |

### Capacity

| Criterion | Passes when | Test |
|---|---|---|
| Spike | Reports sustained commits/s at 60% primary CPU or less, against the required rate from the formula below | Task 6 |
| Target | 5,000 completed payments/s for 60 min; platform overhead p99 ≤ 100 ms and p50 ≤ 25 ms; errors below 0.1%; all applicable checks pass; memory, queues and pool wait flat | L5 |
| Complete path | Same, repeated after Phase 5, with synchronous fraud and the real edge in place | L5 again |
| Overload | 15,000 req/s for 10 min: excess gets 429 or 503, 5,000/s holds, no restart, SLO back within 2 min | L3 |
| Breaking point | Offered load rising to twice L5: the first failing resource is recorded | L6 |
| Reads and callbacks | Floods of each: create-payment keeps its SLO | C48, C49 |
| PSP | G2 is answered in writing by the chosen PSP, or by its sandbox and contract | G2 |

**Required commit rate:** `P × c + R × 2 + K`, where `P` is payments/s, `c` is commits per payment (2 for a sale, 4 for separate authorize and capture), `R` is refunds/s and `K` is callback commits/s. Add outbox publishing as measured in V9. The spike computes this from the configured mix.

### Failure coverage

Every scenario has a detection signal, automatic behavior, recovery and proof in [Failure scenarios](../../failure-scenarios.md#failure-catalogue). This review added:

| Scenario | Failure | Test | Phase |
|---|---|---|---|
| FS-43 | Ambiguous commit | C43 | 1 |
| FS-44 | False not-found from a PSP | C44 | 2 |
| FS-45 | Key retention passes while unresolved | C45 | 2 |
| FS-46 | Crash during callback handling; early callback | C46 | 2 |
| FS-47 | Mass `UNKNOWN` against PSP quota | C47 | 2 |
| FS-48 | Callback flood | C48 | 4 |
| FS-49 | Status-query flood | C49 | 4 |
| FS-50 | Capture and void race; void timeout; lapsed authorization | C50 | 2 |
| FS-51 | Merchant PSP credential revoked or expired | C51 | 2 |
| E25-E29 | Cross-route keys, fraud block, unresolved replay, finished replay, refused request | C31a | 1 |

**Known gaps, with no scenario yet:** failure during partition drop or archive; loss of a large in-flight batch at the broker during a publisher change; client-secret abuse, once designed; and disputes arriving on real payments.

## Benchmark rules

### What a run measures

Admitted req/s, rejected req/s and completed payments/s are recorded separately, as the [standard names](../../glossary.md#standard-names) define them. The target applies to **platform overhead**, measured on the server as total time minus PSP wait. End-to-end latency is recorded against a fixed PSP latency.

### When a result can be trusted

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

A valid run **fails its gates** on any [gate miss](../../scaling-playbook.md#measures-and-gates), including any failed correctness check. A fast run with wrong money is a failed run. [Which checks apply](../../verification.md#correctness-checks) depends on the stage, and an inapplicable check is reported as not applicable, never as passed.

### Test conditions

- **Arrival model:** open, at a fixed rate, so a slow system can't reduce its own load.
- **Workload:** the [example mix](../../scaling-playbook.md#workload-and-load-generation), with stated shares of sale and two-call capture, plus callbacks. Merchant traffic is skewed 80/20, since uniform traffic hides hot rows.
- **Data:** prefilled before measuring, starting at 100M payments on Linux, with the size recorded. An empty database flatters every number.
- **Environment:** Linux with NVMe, WAL on its own volume, quorum replication across zones, and generators on separate machines. Machine sizes are recorded per run.
- **PSP:** fixed latency for comparable runs, with separate runs for variable latency and failures. The mock must be shown to carry at least 25,000 req/s on the same class of hardware, and must model each recovery mode and a delayed request landing late.

### What the spike must answer

| Variant | Question | Answers |
|---|---|---|
| V1 | What does quorum commit cost? | G3, provisional |
| V2, V12 | What does the balance check cost, as a row trigger, a statement trigger or none? | G4, provisional |
| V3 | What does row-level security cost? | Tenant backstop |
| V4 | Does the pool or a PgBouncer process limit first? | Connection budget |
| V5 | Which partitioning scheme holds up? | D25 |
| V6 | Does pipelining statements help? | Transaction design |
| V7 | Can keys expire by batched delete under load? | [D19](../../decisions.md#d19) |
| V8 | How many refunds/s can one hot merchant sustain, now without the lock? Does the same row limit it? | Confirms [D38](../../decisions.md#d38) |
| V9 | Marking rows, a progress marker or log capture for the outbox? | Publisher design |
| V10 | What do callbacks cost, one per transaction or batched? | Capacity model |
| V11 | What does a sale save against two calls? | [D29](../../decisions.md#d29), required rate |

## Stop conditions

Stop and bring the evidence to the owner when:

- G1 says one primary isn't enough. Cells change the roadmap.
- G2 says the PSP can't reach the target. More engineering won't fix a contract limit.
- Meeting a gate would need asynchronous replication, which gives up RPO 0.
- Any run shows a duplicate charge, lost record or imbalance that can't be explained. Correctness work comes before all further load work.

## Unresolved questions

**For the owner:** the [open decisions](#owner-decisions) above, and these:

1. Does v1 carry real money, or is it a reference implementation? If real, disputes need at least recording, a PCI assessor must confirm scope, and legal work starts, because [D35](../../decisions.md#d35) reduces regulatory exposure but doesn't remove it. The docs currently treat disputes as `Proposed` and legal work as out of scope.
2. Who owns the fraud fallback policy and the shed order?
3. What is the retention period for payments and ledger data? G6 can't be answered without it.

**For design, at the stage shown:**

| Question | When |
|---|---|
| Client-side access: client secret format, scopes and lifetime | Stage 4 |
| Test mode isolation mechanism | Stage 5 |
| Manual review tooling for `REVIEW` and stuck attempts | Stage 2 |
| Settlement files that net fees, and fee ledger treatment | With fees |
| Refunds after `SETTLED`, and disputes after a partial refund | With settlement and disputes |
| Merchant onboarding: how a merchant connects a PSP account | With the merchant platform; seeded rows until then |
| Data movement between cells | Only if G1 requires cells |

**For measurement:** every `MEASURE-FIRST` row in the [readiness table](#design-readiness).

**Not verified by this review:** Whether Npgsql sends a cancel when its own timeout fires on a commit is untested, and Task 13 must settle it. Adyen's rate limit and lookup behavior weren't confirmed. The PCI points come from vendor sources and need an assessor. Licensing depends on jurisdiction and needs legal advice.

## Evidence

Read 2026-11-05. Provider details change, so recheck them at adapter time.

| Source | Used for |
|---|---|
| [Stripe rate limits](https://docs.stripe.com/rate-limits) | Finding 12 |
| [Stripe idempotent requests](https://docs.stripe.com/api/idempotent_requests), [advanced error handling](https://docs.stripe.com/error-low-level) | Findings 2, 3 |
| [Stripe Search](https://docs.stripe.com/search) | Finding 2 |
| [Adyen API idempotency](https://docs.adyen.com/development-resources/api-idempotency), [cancels](https://docs.adyen.com/api-explorer/Checkout/latest/post/payments/_paymentPspReference_/cancels), [payments lifecycle](https://docs.adyen.com/account/payments-lifecycle) | Findings 2, 3, 6 |
| [Visa fiscal 2025 annual report](https://s29.q4cdn.com/385744025/files/doc_downloads/2025/Visa-Fiscal-2025-Annual-Report.pdf) | Finding 13. The figure comes through a search summary of the report |
| [Synchronous commit cancel, Michael Paquier](https://paquier.xyz/postgresql-2/track-commit-synchronous/), [pgsql-hackers thread](https://www.postgresql.org/message-id/CALj2ACUO6oz-43ryqfMOVZ_Q-N10C5tkzKku12%2BQV02NnXsDrw%40mail.gmail.com) | Finding 4 |
| [PgBouncer manual](https://manpages.ubuntu.com/manpages/questing/man1/pgbouncer.1.html), [ClickHouse benchmark](https://clickhouse.com/blog/pgbouncer-clickhouse-managed-postgres.md) (vendor) | Variant V4 |
| [Conduktor outbox guide](https://www.conduktor.io/blog/transactional-outbox-pattern-database-kafka.md) (vendor) | Finding 11 |
| [SecurityMetrics on hosted fields](https://www.securitymetrics.com/blog/ultimate-cheat-sheet-making-online-pci-compliance-work-you) (vendor) | Finding 16 |
| [Hyperswitch](https://hyperswitch.io/blog/building-hyperswitch-the-world-s-first-open-source-payments-platform) | Finding 19 |
