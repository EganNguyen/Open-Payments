# Implementation Guide

How to build Open Payments and benchmark it, from an empty repository to a recorded result.

Status: Draft · Last updated: 2026-11-05

**Nothing here is built yet.** Paths and commands below describe what the [plan](superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md) will create, and no command works until its task is done. All numbers are targets or estimates until measured.

## How the documents fit together

| Document | Answers | Use it for |
|---|---|---|
| [Spec](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md) | What counts as done, and when a benchmark can be trusted | Scope arguments, judging a result |
| [Plan](superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md) | Which files, signatures and tests, in which order | The task you are on |
| [Delivery workflow](delivery-workflow.md) | Who does what, and the design, build and review gates | Before starting any task, and whenever you are unsure whether to decide or escalate |
| This guide | How to work through it all | Orientation, setup, running things |
| [Design docs](../README.md#documents) | How the system behaves and why | Any question about behavior |

If they disagree, the design docs win. Don't work around the disagreement. During a build, [stop and escalate](delivery-workflow.md#build-gate) it.

## Before you start

### Owner decisions

Work can't pass these points without an answer. The recommendations are in the [spec](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#owner-decisions).

| Decision | Blocks |
|---|---|
| Where recorded benchmarks run | The Linux runs in Tasks 6 and 17 |
| Which PSP sandbox to use | Stage 2 |

### Tools

| Tool | Needed from | State on the current machine |
|---|---|---|
| Docker | Task 3 | Installed, not running. Start Docker Desktop yourself |
| `psql`, `pgbench` | Task 3 | Installed through `libpq` |
| Python 3 with pytest | Task 5 | Python present, pytest missing |
| .NET 10 SDK | Task 7 | Missing |
| k6 | Task 8 | Installed |
| `kubectl` and a cluster | Stage 4 | `kubectl` installed, no cluster chosen |

Pin the SDK with `global.json` in Task 7. .NET 10 is the [chosen version](decisions.md#d11), so install that channel, not simply the newest SDK:

```bash
curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
```

```bash
brew install k6
```

### Machines

| Machine | Good for | Can't do |
|---|---|---|
| Laptop | Developing, testing scripts, comparing before and after a change | Answer G1, or produce a recorded result. Docker's VM and shared disk change commit timing |
| Rented Linux | The spike, capacity numbers, all recorded runs | Stay on: shut machines down after each run to limit cost |

## Working rules

The full list is in the plan's [Global Constraints](superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md#global-constraints). The ones most easily broken:

1. **Failing test first.** Run it, see it fail for the expected reason, then write the code.
2. **One write path.** Only `OpenPayments.Transitions` writes money state ([D2](decisions.md#d2)).
3. **No network inside a transaction.** The PSP call sits between transactions A and B, never in one.
4. **No business rules in the shell.** Decisions live in `Decide`. The database shell loads, calls and writes.
5. **Checks stay independent.** `OpenPayments.Checks` never references `Domain` or `Transitions`, and its fixtures use raw SQL.
6. **Never relax a gate to pass.** A missed gate is a finding. Record it and fix the bottleneck.
7. **Tick a verification row only with a recorded run.** It needs a run ID, commit, configuration and result link.
8. **Builders don't decide design.** Escalate a missing or conflicting decision, and go no further on that task.

**Git:** commit and push directly to `main`, staging explicit paths. Author commits as `EganNguyen <tuannh2y@gmail.com>`.

## Repository layout

```
api/openapi.yaml                 Draft contract, frozen only after C39
db/spike/                        Spike schemas, pgbench workloads, matrix runner
db/migrations/                   Schema, written from spike results
deploy/compose/                  Replicated PostgreSQL, PgBouncer, metrics stack
scripts/                         bootstrap, settings check, migrate
src/
  OpenPayments.Domain/           Money, IDs, states, Decide. No I/O at all
  OpenPayments.Transitions/      TxRunner and PaymentStore. The only money writer
  OpenPayments.Psp/              IPspAdapter and adapters
  OpenPayments.Api/              Endpoints, PaymentFlow, auth, limits, metrics
tools/
  OpenPayments.MockPsp/          Test PSP with an effect log
  OpenPayments.Checks/           CK1-CK10. Independent of src/
tests/                           Domain, integration, acceptance, checks, mock PSP
benchmarks/                      k6 mix, profiles, run.sh, report, results
```

The dependency direction is `Api` to `Transitions` and `Psp`, and those to `Domain`. `Domain` depends on nothing, and a test enforces it.

## Working through the milestones

### A: Close the design (Tasks 1-2)

**Why first:** the accounts and data-lifecycle choices can't be changed cheaply after the first migration.

- **Read first:** [Stripe research](stripe-research.md), lessons 1, 2 and 3.
- **Do:** record D21-D24 once accepted, then correct the docs and draft the API.
- **Done when:** the docs agree with each other, the verification doc still has 133 unticked rows, and the contract passes lint.
- **Watch for:** freezing the API. It stays a draft until the 3D Secure flow passes ([D17](decisions.md#d17)), because that flow can reshape it.

### B: Run the database spike (Tasks 3-6)

**Why here:** a "no" on G1 changes the architecture, so it comes before any C#.

- **Read first:** [Database standards](engineering-standards.md#database) and [Decision gates](scaling-playbook.md#decision-gates).
- **Do:** model the real transactions in pgbench, one database transaction per pgbench transaction, then run the matrix.
- **Done when:** `benchmarks/spike/REPORT.md` exists from a Linux run, the owner has decided G1, and `0001_initial.sql` exists.
- **Watch for:**
  - Running with `pgbench -R`. Without a fixed rate, a slow database reduces its own load.
  - Empty tables. Prefill first, and record the size.
  - Merging steps into one transaction, which would hide the real commit count.
  - Treating laptop numbers as an answer. The report must say they are comparative only.

### C: Build the test tools (Tasks 7-11)

**Why before the code:** the tools that judge the system should exist before there is a system to flatter.

- **Read first:** [Correctness checks](verification.md#correctness-checks) and [Workload and load generation](scaling-playbook.md#workload-and-load-generation).
- **Do:** build the skeleton and static gates, mock PSP, checks, metrics and harness.
- **Done when:** a harness run with no API produces check failures and a non-zero exit, showing that checks run automatically and can't pass on no data.
- **Watch for:**
  - A mock that answers `unknown_attempt` for an evicted key. The reconciler reads that as "never charged".
  - A check with no corruption test. If it has never failed, it isn't evidence.
  - The mock's capacity. Prove it on benchmark hardware before trusting a Linux run.

### D: Build the payment core (Tasks 12-16)

**Why this order:** `Decide` is pure and fast to test exhaustively, so the rules settle before the database and HTTP layers depend on them.

- **Read first:** [Payment lifecycle](payment-lifecycle.md), [Payment flow](architecture.md#payment-flow) and [Deadlock handling](deadlock-handling.md).
- **Do:** build `Decide`, then the store, then create and attempt, capture and refunds.
- **Done when:** U1-U4, C5, C6, C31a, C43, S1, S3, S5 and S6 pass, and are recorded in `docs/phase-gates/phase-1.md`.
- **Watch for:** the [patterns below](#core-patterns), which cover the mistakes that cost money.

### First benchmark (Task 17)

See [Running a benchmark](#running-a-benchmark). The output is `benchmarks/BASELINE.md`, which states plainly whether the target still looks reachable on one cell.

### After Task 17

Write a new detailed plan at the start of each [roadmap stage](superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md#roadmap-after-task-17), and read the latest measurements first. Each stage repeats the same loop: build, pass its gate, run its benchmark, and record the bottleneck.

## Core patterns

### The shape of one attempt

```
validate request and auth
      |
read the key; a replay answers here
      |
fraud check; a block commits a failed attempt and answers 402
      |
transaction A: idempotency key + payment + attempt   commit
      |
   request token is dropped here; a deadline token takes over
      |
PSP call with attempt_id as the key
      |
transaction B: state + attempt + key result + ledger + outbox   commit
      |
answer the client
```

### Five mistakes to design out

**1. Cancelling after the money has moved.** If the client disconnects after transaction A and the request token still governs the work, the PSP may charge while transaction B never runs. After A commits, use a deadline token only.

**2. Reporting a decline you don't know about.** If transaction B fails after the PSP approved, the customer may have been charged. Answer 202, leave the attempt unresolved, and let recovery find it. Never answer with a decline.

**3. Calling the PSP under a new key.** A retry of the same operation uses the same `attempt_id` or `refund_id`. A new key is a new charge.

**4. Treating "not found" as "not charged".** A delayed request can still land. Only proof fails an attempt ([D27](decisions.md#d27)), and a replay stops well before the PSP's key retention ends.

**5. Cancelling a commit.** [On PostgreSQL 18.6](../benchmarks/spike/commit-cancel.md), a cancel during the synchronous wait returned a normal `COMMIT` success with only a warning, while no standby had the row. So no cancel is ever sent during `COMMIT`. The wait has its own limit, because `statement_timeout` doesn't cover it, and hitting the limit means dropping the connection and treating the outcome as unknown ([D28](decisions.md#d28)).

### Outcomes and answers

The routes are defined in `api/openapi.yaml`, and these codes follow the plan's draft. Change both together.

| Situation | Answer |
|---|---|
| Approved, or captured | 200 |
| PSP requires customer action | 200 with the action |
| Declined | 402 with `failure_code`; the payment can be retried |
| PSP timeout, or transaction B failed after the PSP call | 202 with the payment ID |
| Same key, same request, finished | The stored answer, unchanged |
| Same key, same request, unresolved | The current state, with 202 |
| Same key, different method, route or body | 422 |
| Fraud block | 402 with `fraud_blocked`, and no PSP call |
| Same key, first request running | 409 with `Retry-After` |
| Another attempt or capture is live | Current status, or 409 with `Retry-After` |
| Attempt cap reached | 409 with code `attempt_cap_reached` |
| Merchant over its rate limit | 429 with `Retry-After` |
| In-flight cap reached, pool wait timed out or `55P03` | 503 with `Retry-After` |
| `40001` or `40P01` after two retries, or retry budget spent | 503 with `Retry-After` |
| Void requested while a capture is live, or the reverse | Current status, or 409 with `Retry-After` |
| Merchant's PSP credential rejected | Fails fast with a stable code, with no `UNKNOWN` |
| Conditional update changed no rows | Re-read and answer from current state. Never overwrite |

### Lock order

Always: `payments`, then `refunds` and other child rows, then ledger and outbox ([Prevention](deadlock-handling.md#prevention)). v1 has no per-account lock ([D38](decisions.md#d38)), and adding fees or payouts must bring it back first.

### Adding a state transition

1. Add or find the row in the [transitions table](payment-lifecycle.md#transitions). If it isn't there, it's forbidden until the doc changes.
2. Add its case to the state tests, and let the exhaustive search check the invariants.
3. Implement it in `Decide`, with its ledger lines and outbox event.
4. Add a golden case if an incident or worked example prompted it.

The shell should need no change. If it does, a business rule has probably leaked into it.

## Running a benchmark

Available from Task 11. Run these in order on a clean git tree.

**1. Start the system, and confirm the database is configured as intended:**

```bash
scripts/check-db-settings.sh
```

**2. Run one ramp step:**

```bash
benchmarks/run.sh 3 --env local --stage phase1 --psp-latency 200
```

**3. Read `benchmarks/runs/<run-id>/result.md` in this order:**

| Question | If no |
|---|---|
| Is the run valid? | Fix the tooling and rerun. An invalid run says nothing about the system |
| Did all applicable checks pass? | Stop. Find the cause before any more load work |
| Were the [gates](scaling-playbook.md#measures-and-gates) met? | Use the [bottleneck runbook](scaling-playbook.md#bottleneck-runbook), and change one thing at a time |
| Is the bottleneck written down? | Write it down. It's required before the next step |

**4. Know what you're holding.** Before Phase 3, or on a laptop, a result is a *preview*: useful for comparison, and no proof. The [spec](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#when-a-result-can-be-trusted) defines all three result types.

**5. Compute capacity once step 5 has run on Linux,** using `N = ceil(T / (C × U))` from [Capacity sizing](scaling-playbook.md#capacity-sizing). `T` is about 8,300 req/s with auto-capture at the example mix, and about 16,700 with separate capture requests.

## Finishing a stage

- [ ] The stage's exit tests pass, with results written to `docs/phase-gates/`.
- [ ] Anything not achieved is listed by name.
- [ ] The stage's benchmark ran, and its bottleneck and result are recorded.
- [ ] Verification rows are ticked only where a recorded run proves them, and the [progress counts](verification.md#progress) are updated.
- [ ] Docs changed by what was learned are corrected, with each fact in its one home.
- [ ] The [review gate](delivery-workflow.md#review-gate) has closed, with no open critical or high finding.
- [ ] The next stage's [design gate](delivery-workflow.md#design-gate) writes its plan from these results.

## Common problems

| Symptom | Likely cause | First step |
|---|---|---|
| Everything looks fast, but with few writes | Empty tables, or a transaction merged in the workload | Check prefill size and commits per payment |
| `55P03` rises while deadlocks stay at zero | `deadlock_timeout` isn't below `lock_timeout` | `scripts/check-db-settings.sh` ([D1](decisions.md#d1)) |
| Pool wait rises while database CPU is low | Connections held too long, or PgBouncer at its limit | Check transaction duration and PgBouncer CPU. Look for network calls in a transaction |
| Run marked invalid with dropped iterations | Generator too small, or `maxVUs` below req/s × duration | Add generators, and raise `maxVUs` |
| Completed metric differs from the database | Counter increments before the commit, or a run was reused | Check where the counter fires, and reset to the snapshot |
| A check passes though the data looks wrong | No corruption test, or it's marked not applicable | Add the failing fixture, and check `--stage` |
| CK2 finds a missing effect in a soak | Mock window evicted an attempt, or the effect log lost data | Check the mock's `evicted` count and channel |
| Prepared-statement errors through PgBouncer | Transaction pooling and prepared statements disagree | [Verify the combination](engineering-standards.md#database) before relying on it |
| `Decide` tests are slow or flaky | I/O, clock or random values leaked into `Domain` | Pass them in as inputs |

## When to stop and escalate

Bring the evidence to the owner when G1 or G2 fails, when a gate could be met only with asynchronous replication, or when a run shows an unexplained duplicate charge, lost record or imbalance. The [stop conditions](superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#stop-conditions) give the reasons. Don't work around any of these.
