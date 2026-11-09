# Stage 1 Review Gate

The independent review of Stage 1, as [Delivery workflow](../delivery-workflow.md#review-gate) defines it. The reviewer didn't design or build this stage, and fixed nothing.

Status: **Open: 2 high findings** · Reviewed: `6cc5f8c`..`b4d99a8` (`origin/main`) · Reviewer: Opus, fresh session · Date: 2026-11-09

## Verdict

**Stage 1 can't close.** Two high findings are open, H1 and H2. Both are the same kind of failure: after the PSP may have moved money, the API answers 500 and leaves the operation `PENDING` with its key unresolved, where [D27](../decisions.md#d27) and [D28](../decisions.md#d28) promise a 202 and an attempt that stays findable. No critical finding: the reviewer found no wrong money, no double charge, no lost ledger row and no data shared between merchants.

| Severity | Count | Open |
|---|---:|---:|
| Critical | 0 | 0 |
| High | 2 | 2 |
| Medium | 7 | 7 |
| Low | 6 | 6 |

Findings go back through the gates: the builder fixes each with a failing test first, a design session handles any design question, and the reviewer then re-checks only those findings.

## Findings

Every finding is open. "R1" to "R7" are the reviewer's own tests (see [Reviewer tests](#reviewer-tests)).

### H1. A transaction B failure after a capture, void or refund answers 500

**Severity:** High. A claimed guarantee doesn't hold ([D28](../decisions.md#d28): "after an unknown transaction B, the client gets 202 and the attempt stays findable"; [D1](../decisions.md#d1): a `55P03` returns 503 with `Retry-After`).

**Evidence:** `src/OpenPayments.Api/PaymentFlow.cs:341` (capture), `:413` (void) and `:492` (refund) call `store.ApplyAsync` after the PSP answer with no handler. The same is true of the PSP-timeout branches at `:322`, `:398` and `:476`. `Program.cs` has no exception handler, so a `BusyException` or `CommitOutcomeUnknownException` becomes a 500 with no `Retry-After`. `c66da95` fixed transaction A only, and says so: "failures after the PSP call keep their own handling". Authorize and sale do handle it (`PaymentFlow.cs:664`, answering 202).

**Reproduction:** R1. Authorize a payment, then capture it. While transaction B runs, another session holds the payment row with `select ... for update`. Under [D54](../decisions.md#d54)'s 200 ms `lock_timeout`, B fails with `55P03`. Observed:

```
capture answered 500, Retry-After=
attempt states: authorize:SUCCEEDED,capture:PENDING
key resolved: null
same-key retry answered 409: {"code":"request_in_flight", ...}
```

The PSP recorded the capture. The merchant is told 500, the attempt stays `PENDING`, and every retry with the same key answers 409, with nothing in Stage 1 to resolve it.

**Why it matters:** D54 makes this reachable. The 50-way C6 refund race already hit `55P03` in transaction A in CI (`c66da95`'s message). Transaction B takes the same row lock. A merchant that reads 500 as "failed" may refund or charge again by other means. The one-live-capture index does stop a second capture, which is why this isn't critical.

### H2. A PSP transport failure other than a timeout answers 500

**Severity:** High. [D27](../decisions.md#d27): anything other than an explicit decline, confirmed cancel or authoritative not-found is inconclusive, so the attempt is `UNKNOWN` and the client gets 202.

**Evidence:** `MockPspAdapter.SendAsync` (`src/OpenPayments.Psp/MockPspAdapter.cs:147` and `:158`) maps only 429, non-2xx status and an empty body to `PspUnavailableException`. An `HttpRequestException`, such as a connection reset after the request was sent, and a `JsonException` from a malformed body escape it. Every route's catch filter (`PaymentFlow.cs:319`, `:395`, `:473`, `:623`) accepts only `OperationCanceledException`, `PspTimeoutException` and `PspUnavailableException`, so these become a 500. The attempt or refund stays `PENDING`, the key stays unresolved, and same-key retries answer 409, as in H1.

**Reproduction:** Not executed: the acceptance fixture's `TestPsp` can't throw a transport exception. To reproduce, point `PSP_URL` at a listener that accepts the request and then resets the connection, and send `POST /v1/payments` with a method. Expected: 202 with the payment `UNKNOWN`. Read from the code, the answer is 500 with the attempt `PENDING`.

**Why it matters:** A reset after the request was sent is the textbook ambiguous outcome. The PSP may have charged the card, while the merchant gets a 500, which clients commonly treat as retryable with a new key ([E5](../failure-scenarios.md#edge-cases) creates a separate payment). That is a path to a double charge that D27 exists to close.

### M1. Two-call auto-capture replays a different answer, and a failed capture is never retried

**Severity:** Medium. [D31](../decisions.md#d31): a replay of a finished operation returns the stored answer.

**Evidence:** `PaymentFlow.cs:96-113`. When the adapter has no sale, the create key is resolved with the authorization's 200 (`ExecuteAttemptAsync`), and the capture runs under a second key, `key + ":capture"`. R4, with a declined capture:

```
first  402 {"state":"AUTHORIZED", "latest_attempt":{"state":"FAILED","failure_code":"capture_declined"}, ...}
replay 200 {"state":"AUTHORIZED", "latest_attempt":{"state":"SUCCEEDED"}, ...}
```

If the capture instead answers 503 (busy) or 202, a retry of the create returns the stored authorization and never captures. A merchant key ending in `:capture` would also collide with the internal key.

**Why it's medium:** The mock adapter declares `SupportsSale: true` (`MockPspAdapter.cs:32`), so no Stage 1 deployment reaches this path, and [D47](../decisions.md#d47) leaves it out of the mix. It must be fixed before the first PSP without sale.

### M2. The new-attempt route answers 500 on a lock timeout

**Severity:** Medium. [D1](../decisions.md#d1): a `55P03` returns 503 with `Retry-After`. `c66da95` says it fixed "every money route", but `CreateAttemptAsync` (`PaymentFlow.cs:154-208`) still catches only `CommitOutcomeUnknownException`.

**Reproduction:** R2. Hold the payment row from another session, then `POST /v1/payments/{id}/attempts`. Observed 500 with no `Retry-After`; expected 503.

**Why it's medium:** It fails before any PSP call and the transaction rolls back, so the key is free and a retry works. No money moves.

### M3. Unknown outcomes mark their idempotency key resolved

**Severity:** Medium, latent until the key-expiry job exists. [D19](../decisions.md#d19): a key is never expired while its operation is unresolved. `resolved_at` is what the expiry index `idempotency_keys_expirable` reads.

**Evidence:** `FromAppliedAsync` (`PaymentFlow.cs:690`) and `FromRefundAppliedAsync` (`:528`) store the 202 answer of a PSP timeout through `ResolveKeyAsync`, which sets `resolved_at = now()` (`PaymentStore.cs:960`). R3: after a timed-out create, the payment is `UNKNOWN` and its key has `resolved_at` set.

**Why it matters:** Stage 1 has no expiry job, so nothing is deleted yet. Stage 2's job, built to D19 and [E3](stage-1-escalations.md#e3-task-16-idempotency-key-retention-has-no-implementation)'s answer, would expire these keys after 24 hours while the payment is still `UNKNOWN`. A later retry would then look new and could charge twice. Every unknown key written in Stage 1 already carries the wrong value. E3's answer asks Stage 2's design gate to check that every resolving path sets `resolved_at`; it should also check that no unresolved path does.

### M4. A replay of an unresolved key answers 409 forever, and keys can be left unresolved

**Severity:** Medium. [D31](../decisions.md#d31) and edge case E27: a replay of an unresolved operation renders the current state. Edge case E3: after a client disconnects, the result is stored and a retry returns it.

**Evidence:**

- Every route turns `KeyLookup.Unresolved`, which already carries the payment, into 409 `request_in_flight` (`PaymentFlow.cs:52`, `:167`; `PaymentStore.cs:179`, `:1146`). R1 shows it. The E27 test (`Task14Tests.cs:156`) passes only because a PSP timeout stores its 202 as a resolved answer, which is M3.
- Key resolution is a separate statement after the state commit (`FromAppliedAsync`, `FromRefundAppliedAsync`, `CancelAsync` at `:253`), so a crash between them leaves the key unresolved.
- Refund and cancel resolve the key with the request's token (`PaymentFlow.cs:253`, `:481`, `:493`, `:528`). A client that disconnects during the PSP call cancels that write after the state has committed. This is E3's case, and it is read from the code, not executed.

**Why it matters:** With no reconciler in Stage 1, such a key answers 409 to every retry, and the merchant can't learn the result through its key.

### M5. The console audit can record a different refund amount than the one sent

**Severity:** Medium. The console records intent before the call ([D40-D45](../decisions.md#d40)), so the audit log can be trusted to say what a person did.

**Evidence:** `src/OpenPayments.Console/Pages/Payments/Action.cshtml.cs:155-158`. When an intent already exists for the form's key, the post skips the state, refundable and confirmation checks and takes the amount from the submitted form. After a `Busy` answer (`:204-208`), the form is shown again with an editable amount (`Action.cshtml:30`). The outcome row copies the intent's amount (`AuditWriter.cs:64-68`).

**Reproduction, read from the code:** refund 1.00 while the API is busy, so it answers 503 and the API stores nothing for the key (D31). Change the amount to 50.00 and send again. The API refunds 50.00 under the same key, while the intent and outcome rows both say 1.00.

**Why it's medium:** It needs an operator or approver, and the API still enforces the refundable limit, so no money beyond the payment moves. The audit record is wrong.

### M6. The test fixtures use the timeout pair D1 forbids

**Severity:** Medium: the suite proves less than it claims. [D1](../decisions.md#d1) sets `deadlock_timeout` to 100 ms so that deadlocks are detected before a 200 ms `lock_timeout` aborts the waiter.

**Evidence:** `AcceptanceFixture.cs:71`, `DbFixture.cs:45` and `C43Fixture.cs:110` set `op_app`'s `lock_timeout` to 200 ms (added in `0056f0d`). None sets `deadlock_timeout`, so the containers run PostgreSQL's default of 1 s. That is the pair the [deadlock lab](../../benchmarks/deadlock-lab/README.md) shows turning deadlocks into `55P03`.

**Why it matters:** No test runs the production pair, so the `40P01` retry path is tested only by the deadlock lab, which ran before D54 (L2).

### M7. The Task 17 record claims a limit from an invalid run

**Severity:** Medium. The evidence supports less than the progress line says.

**Evidence:** The Task 17 line in [progress](stage-1-progress.md) says step 6 "fails its p99 gate: 173.47 ms at 500.03 completed payments/s" with no validity label. That run, `20261109T082010Z-7323d59-s6-5db4`, is invalid on the completed count ([BASELINE](../../benchmarks/BASELINE.md) line 28, and the progress "Step 6 rerun" row). [BASELINE](../../benchmarks/BASELINE.md) line 30 still says step 6 "must be rerun three times from a reset before D51's milestone or Task 17 can close", which contradicts the amended [D53](../decisions.md#d53). Steps 2 and 4 were not run in this search.

**What the evidence supports:** in a run invalid on its count, p99 was over the 100 ms gate at about 500 completed payments/s, with one API instance near two cores. Under the amended D53, the owner may close Task 17 on that. The record should say the run is invalid, and BASELINE line 30 should be corrected. No capacity number is claimed anywhere, which is right.

### L1. `c66da95` has no progress line, and the recorded test counts are stale

`c66da95` changed `PaymentFlow.cs`, the C6 test and CI after the Task 18 line was written. The build gate requires a line per change. The Task 18 line gives 365 tests with 57 acceptance tests. From a clean checkout of `b4d99a8`, the reviewer counted 369, with 61 acceptance tests.

### L2. The deadlock lab hasn't been rerun since D54

The lab's results (progress, `002aba8`) were measured with the runner's 2 s `lock_timeout` override, which D54 removed. Its README says to rerun before comparing. That hasn't happened, so the lab's evidence for D1's retry behavior predates the setting in force.

### L3. Abandoned commits aren't logged

[E1](stage-1-escalations.md#e1-task-13-npgsql-commit-cancellation-breaks-d28) item 5: the continuation "logs it". `TxRunner.ObserveAbandonedAsync` (`src/OpenPayments.Transitions/TxRunner.cs:275`) writes the late failure to `Debug.WriteLine` only, which a release build drops, and logs nothing on a late success. The metrics are updated.

### L4. Commit warnings have no alert

E1 item 6 requires a commit warning to "raise the page signal". `op_commit_warnings_total` and `op_abandoned_commits` are served (`Metrics.cs:121-126`), but `deploy/compose/prometheus` has no alert rules at all. If alerting belongs to a later stage, the owner should say so.

### L5. Capture, void, refund and cancel bypass the in-flight limit

[D34](../decisions.md#d34) gives requests a concurrency limit. `InFlightLimiter` is entered only by create and new attempt (`PaymentFlow.cs:34`, `:149`).

### L6. Build deviations recorded in the progress log

| Deviation | Judgment |
|---|---|
| Tasks 14-16 committed together in `6eb4bd3` | Acceptable. History can't show test-first, but the behavior is covered and H1/H2 aren't caused by it |
| Task 18: only C4 seen failing first; C6-C13 tests written alongside the code | Acceptable with care. The reviewer re-checked console security directly (see below) and found M5, which no console test covers |
| Migration `0003_console.sql` not `0002` | Acceptable: `0002` went to D50 |
| Stage 1 work on 2026-11-09 built by Opus under the owner's exception | Recorded. This review is the independent check that the exception requires |
| Reviewer tests not committed in `tests/review/` | A deviation by this review: the brief said to commit this record alone. The tests are kept for the builder (see below) |

## What the reviewer verified, and how

**Build and tests, from a clean worktree of `b4d99a8`:** `dotnet build OpenPayments.slnx -warnaserror` gave 0 warnings and 0 errors. `dotnet test OpenPayments.slnx` passed 369 tests: Domain 70, analyzers 1, mock PSP 17, checks 24, console 123, integration 39, console browser 34, acceptance 61. Docker ran the Testcontainers fixtures.

| Area | What was checked | Result |
|---|---|---|
| Payment flows | Create, sale, authorize, new attempt, capture, void, cancel and refund traced through `Program.cs`, `PaymentFlow.cs`, `PaymentStore.cs` and `Decide.cs` against the lifecycle | Transitions and conditional updates hold. Failure paths after the PSP call fail: H1, H2, M1. Callbacks and recovery don't exist in Stage 1 ([D46](../decisions.md#d46)), so they weren't traced |
| Money | Postings per flow, the deferred balance trigger, grants, refund reservation and limits; R5 and R6 | Balanced per transaction and currency; `op_app` can't update or delete ledger rows (R5); refund limit enforced by `Decide` and the payment's conditional update. No money finding |
| Idempotency | Every key path in the store and flow, against D31 and E1-E29 | M1, M3, M4 |
| Transactions | `TxRunner` against E1's ten requirements and D28: `Command Timeout=0` enforced, keepalive, `CommitAsync(None)` plus `WaitAsync`, abandoned commits handed off, warnings counted, retry only `40P01`/`40001`, `55P03` to `BusyException`; D54's override removed (`TxRunner.cs:297`) | Holds, except L3 and L4. No network call inside a transaction. State, ledger and outbox commit together; the key's answer doesn't (M4) |
| Concurrency | One-live indexes against their paths; capture against void, 5 forced rounds (R6); the deadlock lab's design | One winner every round, no 500, ledger balanced. M6, L2 |
| API | Status codes and `Retry-After` against `api/openapi.yaml` | H1, H2 and M2 return undocumented 500s |
| Security | Tenant filters on every load (R7: merchant B gets 404 for merchant A's payment, with no PSP call); no card data in logs (E16 test) | Holds |
| Console | No project reference to `OpenPayments.Transitions` (the console has no project references); MFA by `amr` or `acr` per D55, with `acr`, `amr` and `auth_time` kept; every policy resolves `console.staff` per request, with a fallback policy; re-authentication by `auth_time` with `prompt=login` and `max_age`; CSP self-only, `frame-ancestors 'none'`; Razor antiforgery; audit intent written before the API call, and a failed intent stops it | Holds, except M5 |
| Checks | `tools/OpenPayments.Checks` references no project, so the CKs are independent of the code they judge | Holds |
| Benchmarks | Task 17 and C13 records against BASELINE and the run files | M7. The C13 pair is valid and supports its narrow claim. **Not rerun:** the brief forbade running benchmarks, so the workflow's "rerun at least one run" wasn't done |
| Observability | Metric names served against E1 | L4. Metrics weren't compared with the database |

**Escalations:** E1-E10 were each read against the code. E1's runner requirements hold (above). E9 is fixed in `0056f0d` and pinned by `D54_runner_uses_the_role_lock_timeout`. E10's `acr` handling holds, at `Program.cs:118-121` of the console. E2, E3 and E4 are closed for Stage 1 by D46 and D53, and no Stage 1 code claims them.

## Reviewer tests

`ReviewerStage1Tests.cs`, written by the reviewer and run in the acceptance project against a clean worktree. It isn't in the repository: per the brief, this commit holds only this record. A copy is in the session notes folder for the builder to add under `tests/review/`, and failing tests must stay failing until their finding is fixed.

| Test | Area | Result | Finding |
|---|---|---|---|
| R1 capture transaction B lock timeout isn't a 500 | Payment flows, transactions | **Fails**: 500 | H1 |
| R2 new attempt lock timeout answers 503 | API | **Fails**: 500 | M2 |
| R3 an unknown payment's key isn't resolved | Idempotency | **Fails**: `resolved_at` set | M3 |
| R4 two-call auto-capture replay matches the first answer | Idempotency | **Fails**: 402, then 200 | M1 |
| R5 `op_app` can't update or delete ledger rows | Money | Passes | — |
| R6 concurrent capture and void: one winner, balanced ledger | Concurrency | Passes, 5 rounds | — |
| R7 merchant B can't read, capture or refund merchant A's payment | Security | Passes | — |

## Sign-off

The gate closes when no critical or high finding is open and the owner has accepted the rest in writing.

| Accepted by | Date | Findings accepted |
|---|---|---|
| | | |
