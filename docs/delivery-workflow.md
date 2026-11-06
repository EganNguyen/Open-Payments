# Delivery Workflow

Who does what, and what must be true before work moves on. Every stage passes through three gates.

Status: Draft · Last updated: 2026-11-05

"Gate" here means this workflow. "Phase" keeps its meaning from [build phases 0-5](verification.md#build-phases).

| Gate | Done by | Purpose | Exit |
|---|---|---|---|
| [Design](#design-gate) | Opus | Reason it through, and measure what design depends on | Owner-approved record |
| [Build](#build-gate) | DeepSeek | Build the approved tasks, test first | All tasks done with evidence |
| [Review](#review-gate) | Opus, in a fresh session | Verify everything independently | No open critical or high findings; owner accepts the rest |

**Each gate is a different job.** The builder doesn't change the design, and the reviewer doesn't fix what it finds. DeepSeek is run outside the Claude session that does design and review, so nothing here depends on either session's memory.

## Stages

| Stage | Contains | Plan |
|---|---|---|
| 1 | Spike, test tools, payment core, first ramp steps | [Tasks 1-17](superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md) |
| 2 | PSP and recovery | Written at its design gate |
| 3 | Async workers | Written at its design gate |
| 4 | Hardening, and the target load tests | Written at its design gate |
| 5 | Security, operations, and the sign-off number | Written at its design gate |
| C | Staff console | [Tasks C1-C13](superpowers/plans/2026-11-06-console.md). Its place in the order is an owner decision in the [gate record](gates/stage-c-design.md) |

**A stage can't start until the previous stage's review gate has closed.** Work goes straight to `main`, so review can't block a merge. It blocks the next stage instead, and nothing carries real money until every stage has closed.

Records live in `docs/gates/`, one per gate per stage. They are in the repository so any session or tool can read them.

## Design gate

1. Read the design docs and the latest measurements. Challenge earlier decisions.
2. Analyze requirements, trade-offs, financial invariants and failure scenarios.
3. Design service boundaries, APIs, schemas, transactions and concurrency controls.
4. **Run any measurement the design depends on.** Spike scripts and environment checks belong here, because their results are design decisions.
5. Record decisions in [Decisions](decisions.md), and write tasks with exact files, signatures and tests, plus acceptance criteria.
6. List unresolved decisions and risks in `docs/gates/stage-N-design.md`, including every decision made without the owner.

**Exit:** the owner has approved the record, and it names the commit the build starts from. `MEASURE-FIRST` items that this stage's tasks depend on are measured, and nothing they depend on is `NEEDS-OWNER`.

## Build gate

Give the builder this section, its stage's plan and the spec.

**Read first, in order:** the [spec](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md), `docs/gates/stage-N-design.md`, the plan's Global Constraints and Review Focus, then the [implementation guide](implementation-guide.md). Read the design docs as a task points to them.

**For each task, in order:**

1. Write the test the task names, run it and watch it fail for the expected reason.
2. Write the code, run the test and watch it pass.
3. Run the whole test suite, static analysis and any integration check the task names.
4. Commit and push to `main`, with explicit paths and the author in the plan's constraints.
5. Add one line to `docs/gates/stage-N-progress.md`: task number, commit, test command, result.

**Don't change without approval:** `docs/decisions.md`, the spec, the API contract, `db/migrations/` beyond what a task names, or a gate's value. Don't weaken a test to make it pass.

**Stop and escalate,** adding an entry to `docs/gates/stage-N-escalations.md` and going no further on that task, when:

- A requirement conflicts with another, or with the design.
- A design decision is missing, or an interface doesn't match its task.
- A test can't pass without changing what it asserts.
- A [stop condition](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#stop-conditions) occurs.

Each entry gives the task, what conflicts, the evidence, and the options seen. A design session answers it, and the answer is recorded before work continues. Tasks that don't depend on it can go on.

**Benchmarks:** record every run in `benchmarks/`, using the [trust rules](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#when-a-result-can-be-trusted), including invalid and failed runs.

**Exit:** every task has a progress line with evidence, the whole suite passes from a clean checkout, and no escalation is open.

## Review gate

Start a fresh session with no memory of the design or build. Review the complete change since the stage's starting commit, not just the last commit. **Don't fix anything.**

| Area | What to verify |
|---|---|
| **Payment flows** | Trace create, sale, new attempt, capture, void, refund, callback and recovery end to end through the code, against the [lifecycle](payment-lifecycle.md) |
| **Money** | Ledger balances per currency, is append-only and follows the fund flows; refund limits hold; CK checks are independent of the code they judge |
| **Idempotency** | Every answer in [D31](decisions.md#d31), across concurrency, crashes and replays; no route reaches the PSP under a new key on retry |
| **Transactions** | No network call inside a transaction; state, ledger and outbox commit together; no cancel reaches `COMMIT`; lock order holds |
| **Concurrency** | Races, deadlocks, retries and recovery, forced rather than assumed. Check each one-live-attempt index against its path |
| **Failure handling** | The plan's Review Focus, and each failure scenario this stage claims |
| **API** | Behavior matches `api/openapi.yaml`, including error codes and `Retry-After` |
| **Security** | Tenant isolation, secrets, log contents, card data never reaching us |
| **Observability** | Metrics match the database; every alert in scope can fire |
| **Benchmarks** | Validity rules really run; a preview isn't presented as proof; rerun at least one run yourself and compare |

**Run independent tests.** Write at least one test per area that the builder didn't write, kept in `tests/review/`. Don't rely on the builder's suite alone. Reviewer tests that fail stay failing, and are named in the finding.

**Grade each finding by what a person using the system would get:**

| Severity | Meaning | Effect |
|---|---|---|
| Critical | Wrong money, a double charge, a lost record, or data exposed between merchants | Blocks the next stage |
| High | A claimed guarantee, gate or SLO doesn't hold, or a benchmark result can't be trusted | Blocks the next stage |
| Medium | Real defect with limited effect, or a test that proves less than it claims | Fix, or the owner accepts it |
| Low | Quality or clarity | Owner's discretion |

**Every finding has:** severity, evidence (file and line, or output), reproduction steps, and the invariant or criterion it breaks. A finding with no way to reproduce it is marked as such.

**Findings go back through the gates.** The builder fixes them with a failing test first, or a design session handles any that are design problems. The reviewer then re-checks only those findings.

**Exit:** `docs/gates/stage-N-review.md` has every finding and its state, no critical or high finding is open, and the owner has accepted what remains in writing.
