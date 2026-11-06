# Open Experiments

What's still unmeasured on the way to proving maximum sustainable payments/s, what has already been compared, and what order to run the rest in. How to run things is in the [benchmark guide](benchmark-guide.md).

Status: **No capacity number is proven yet.** Every result so far is a preview · Last updated: 2026-11-06

The repository rarely says "experiment". These are its spikes, variants (V), ramp steps, load tests (L), chaos tests (C) and comparisons.

## What must be proven

**The claim:** the highest completed payments/s the platform sustains while every gate holds. Requests aren't payments: at the [D47](../decisions.md#d47) mix, half of all requests start a payment.

The criteria are already set, so a run is judged against these and not new ones:

| Criterion | Value | Source |
|---|---|---|
| Rate | 5,000 completed payments/s | [L5](../verification.md#load-tests) |
| Duration | 60 min, then a 24 h soak | L5, L2 |
| Platform overhead | p50 ≤ 25 ms, p99 ≤ 100 ms | [Capacity criteria](../superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#capacity) |
| Errors | Below 0.1%, excluding deliberate rejections | [Gates](../scaling-playbook.md#measures-and-gates) |
| Money | All applicable CK checks pass. Any failure fails the run, however fast | [Trust rules](../superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#when-a-result-can-be-trusted) |

**Only a Recorded run proves anything:** valid, on Linux, from Phase 3 onward, with all checks applicable. A laptop run is a preview, useful for comparing runs and finding bottlenecks. The number finally reported is L5 repeated after Phase 5 ([D24](../decisions.md#d24)).

## The seven experiments

| Experiment | Repository name | Status | Waiting on |
|---|---|---|---|
| Baseline | Ramp steps 0-1 | **Stale.** Old-mix runs in [BASELINE](../../benchmarks/BASELINE.md); not rerun at D47 | Index comparison |
| Index comparison | Task C2 | **Stopped early by the owner.** No cost detected in three valid runs ([details](#index-comparison-task-c2)) | Linux, for `EXPLAIN` checks only |
| Concurrency | V4 pools, step 6 instance scaling | Database pools partly measured, with aborts. No API-level or instance-scaling run | Steps 5-6 |
| Load | L1, ramp steps 0-9 | **At D47, only steps 2 and 4 have valid runs.** Both previews | Linux for `C` and `N` |
| Stress | L6 (step 11), L3 | Not run | Phase 4 |
| Soak | L5 (60 min), L2 (24 h) | Not run. Longest API run is 10 min, longest spike run 60 s | Phase 4, Linux |
| Recovery | L3's 2-min SLO return, C1, C2, C7, C10, C11 | Not run. C43 and the C5/C6 correctness tests pass | Phase 4 |

### Run order

Stress and soak are expensive, so they run only on the final candidate:

1. Ship `refunds_payment` under [D50](../decisions.md#d50), then fix the check query that scans `outbox` once per payment ([order](../gates/stage-1-progress.md#before-the-limit-search-2026-11-06)).
2. Rerun steps 0, 1 and 3 at D47. [E4](../gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) asks for steps 0-3 first, and only 2 and 4 have run.
3. Continue the laptop limit search through steps 5, 6 and 7. Stop at the first failed gate or invalid run.
4. For each bottleneck, change one thing and compare before and after at the same step, mix, configuration and database size.
5. On Linux, repeat the mock capacity run, then steps 4-5 for `C` and `N`.
6. After Phase 4, run L1, L5, L6, L3, L2 and the failure suite on the final candidate.
7. After Phase 5, repeat L5. That's the reported number.

## Metrics

| Metric | Recorded by | State |
|---|---|---|
| Completed payments/s | `result.md`, checked against the database count | Works |
| p50, p95, p99 | `result.md`, platform overhead and end-to-end | Works |
| Error rate | `result.md` | Works |
| Duplicate charges | CK2, C5 | Works |
| Refund correctness | CK3, C6 | Works |
| CPU, memory | `resources.json` | Works |
| Pool wait, deadlocks | `resources.json` | Works |
| **Lock waits** | `pg_locks_count` | **A proxy.** It counts locks held, not waits |
| **Queue lag** | Outbox age, consumer lag | **`n/a`** until Phase 3 |
| **Mock added p99** | `MOCK_ADDED_P99_MS` | Set by hand from the laptop preview. E4 requires a value measured for the run on Linux |

## Open comparisons

### Index comparison (Task C2)

Does adding the four console indexes change sustained throughput or p99? The pass condition is no measurable change ([spec](../superpowers/specs/2026-11-05-console-design.md#measurements)).

The indexes are `payments_review`, `payment_attempts_review`, `refunds_review` and `refunds_payment`. The console needs them, so **this measures their cost and doesn't pick a winner.** `refunds_payment` ships regardless, and earlier, under [D50](../decisions.md#d50).

| | |
|---|---|
| Design | Step 4 (250 req/s for 10 min), D47 mix, runs ordered without, with, with, without so database growth cancels out |
| Status | **Stopped early by the owner, no cost detected.** Three valid runs, only one of them without the indexes. Run 4 was stopped during its checks and is invalid |
| Output | [Console index comparison](../../benchmarks/console-indexes.md) |
| Trust | Laptop preview, approved by the owner on 2026-11-06. **The owner decided not to repeat it on Linux** |
| Also open on this gate | Clickable prototype review, owner's signature ([gate](../gates/stage-c-design.md#must-finish-before-the-build)) |

This can show a large cost but can't rule out a small one, because the without, with, with, without order lost its last run. So these stay open:

1. **On the first Linux run, check instead of repeating.** Confirm with `EXPLAIN` that the queries use these indexes, and watch `refunds_payment`'s cost in the normal results. If it looks significant, bring it to the owner ([gate](../gates/stage-c-design.md#risks-to-watch)).
2. **Fix the check query.** Check time rose from 3.4 to over 67 min across the four runs, mainly from a query that scans `outbox` once per payment. Unfixed, it would also stall long ramp steps and soaks.
3. **Check run 3's `DB CPU 0.02`.** Runs 1 and 2 report 1.96 and 1.79 at the same load, so this looks like a collection fault, not a real drop. It's in the run's `result.md`, and the comparison doesn't rely on it.

### Other comparisons

| Comparison | Status | Source |
|---|---|---|
| Steps 0, 1, 3 and 5-7 at D47 | Not run | [E4](../gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) |
| Linux steps 4-5 (`C`, `N`) | **Blocked:** no Linux host | [E4](../gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) |
| Mock PSP at 25,000 req/s, under 5 ms added p99 | Unproven. Valid preview at 10,000 req/s, 1.34 ms | [Mock capacity](../../benchmarks/mock-psp-capacity.md) |
| Commits per payment against 2.8 | Needs Linux steps 4-5 | [E5](../gates/stage-1-escalations.md#e5-task-17-workload-mix-has-no-agreed-saletwo-call-shares) |
| Console searches on the replica under load | Not run. Task C13 Step 5, with its own output file | [Plan](../superpowers/plans/2026-11-06-console.md#task-c13-audit-log-and-staff-screens-then-the-finish-checks) |
| One cell against two | Smoke test only, no results. Both share one SSD | [Guide](benchmark-guide.md#5-run-the-suite) |
| Schema C at pools 200 and 400 | Pool 200 only. The pool 400 run was deleted before recording anything | [Spike](spike/macbook-2026-11-05/summary.md#schema-a-the-original-design-at-the-same-rates) |
| Schema C result repeated | One run so far | [Spike](spike/macbook-2026-11-05/summary.md#schema-a-the-original-design-at-the-same-rates) |

## Spike variants

The [matrix](../../db/spike/matrix.json) has 20 entries. Only `baseline` and a short `V13` have run on the native stack.

| Variant | Question | State | Decides |
|---|---|---|---|
| V1 | What does quorum commit cost? | Not run. Cost only, can't be adopted | G3, provisional |
| V2, V12 | Balance check as a row trigger, statement trigger or none? | Not run. V2 is cost only | G4, provisional |
| V3 | What does row-level security cost? | Not run | Tenant backstop |
| V4 | Does the pool or a PgBouncer process limit first? | Partial, from limit searches | Connection budget |
| V5 | Do time partitions hold up? | Not run. D25 chose schema C without it | D25 |
| V6 | Does pipelining help? | Docker, sale only, one run each: +24%. Not with real flushes | Transaction design |
| V7 | Can keys expire by batched delete under load? | Not run. **Required at Stage 2's design gate** | D19 |
| V8 | How many refunds/s from 1, 100 or 10,000 merchants? | Not run | D38 |
| V9 | Outbox by marking rows, a marker or log capture? | Not run. **Required before Stage 3's publisher** | Publisher design |
| V10 | Callback cost, one per transaction or batched? | Not run | Capacity model |
| V11 | What does a sale save against two calls? | Not run | D29, required rate |
| V13 | Full mix capacity | Short smoke only. Needs Linux | G1, G6 |

## Open bottlenecks

From the [spike](spike/macbook-2026-11-05/summary.md#bottlenecks):

| # | Bottleneck | State |
|---|---|---|
| 4 | Relation-extension lock | Largely fixed by schema C, in one run. Repeat on Linux |
| 5 | Hot last pages (`BufferContent`) | Fix unmeasured: schema C has no fast-flush run |
| 6 | Non-HOT updates, from `state` in partial-index predicates | Cost not measured |
| 7 | `MultiXactGen` waits, up to 10% | Cause not investigated |
| 8 | Statement round trips | Not measured with real flushes |
| 9 | SSD flush | Saturated on this Mac. Only different hardware helps |

Also unresolved: prepared statements aborted at both pools tried, `commit_delay` had one inconclusive run, and the native "unsafe-fsync" run is mislabeled and must not be used. Its script is fixed but it hasn't been rerun.

## Production validation

These come from the [spike's list](spike/macbook-2026-11-05/summary.md#still-needs-production-validation), and none has been run:

| Validate | How | Blocks |
|---|---|---|
| Durable flush time on real disks | `pg_test_fsync` | Everything below. **The biggest assumption** |
| Commits/s at real flush and zone delay | Linux spike, 100M rows, separate generator | G1 |
| Full-mix capacity | V13 on Linux | G1, G6 |
| One cell at 1,250-2,000 payments/s through the API | L1, L5 | Stage 4 |
| Scaling across cells | Several cells on their own disks | Stage 4 |
| Behavior through checkpoints and vacuum | 60 min run | L5 |
| API CPU per payment and real unmodeled writes | Platform benchmark | Sizing |
| Failover under load | C-series | Production |

## Finished comparisons

| Comparison | Result | Limit |
|---|---|---|
| Schema A against schema C | C removes nearly all aborts. Its p99 is inside A's range at 1,500/s | One run of C |
| Pool 50 to 400 | 1,029 to 3,794 scripts/s, 3.7× | Overload, with aborts. Specific to this disk |
| 1 against 4 PgBouncer processes | One process hit 0.99 core; four reached 4,880 scripts/s | Docker, not durable |
| Generator outside against inside the VM | 2,018 to 3,319 scripts/s, so port forwarding was a limit | Docker, not durable |
| Docker sizing, 14 CPUs and 20 GB against 18 and 40 | No gain | Docker, not durable |
| Fast flushes against real flushes | About 9,750 against 3,232 commits/s, pool 100 | Docker isn't durable, so never a result |
| Mock at 10,000 against 25,000 req/s | 10,000 is a valid preview; 25,000 is invalid on this laptop | Laptop. 25,000 is unproven, not disproven |
| S3 query counts per endpoint | Create 29, read 4, capture 37, refund 37, sale 29, cancel 16, void 33, attempt 34, replay 2 | Acceptance fixture ([record](../phase-gates/phase-1.md#checklist)) |
| Npgsql commit-cancel probe | A commit can be abandoned with no cancel. Settled [D28](../decisions.md#d28) | Behavior, not capacity ([probe](../../benchmarks/npgsql-commit-probe/README.md)) |

## Records to correct

These don't change any result, but they disagree with the run directories or each other:

| Record | Says | Actually |
|---|---|---|
| [BASELINE](../../benchmarks/BASELINE.md) | Only old-mix runs | E4 requires each laptop run recorded there. The D47 runs aren't |
| [Stage 1 progress](../gates/stage-1-progress.md) and [Phase 1](../phase-gates/phase-1.md) | No run at the new mix | [E7](../gates/stage-1-escalations.md#e7-benchmark-reset-leaves-the-ck1-ledger-manifest-stale) records a passing step 2 |
| [Stage 1 progress](../gates/stage-1-progress.md) | E2 and E3 remain open for the build gate | Both are marked answered by D46, and the same note was removed from Phase 1 in `1dda41e` |
| `benchmarks/runs/` | Git-ignored | Results from D47 runs exist only on this laptop until they're copied into a tracked record |
| [Benchmark guide](benchmark-guide.md#the-full-platform) | The API isn't built, so it covers only the database tier | Tasks 7-16 are complete and the k6 harness runs against the API |
