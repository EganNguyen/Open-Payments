# Stage 1 Review Gate

The independent review of Stage 1, as [Delivery workflow](../delivery-workflow.md#review-gate) defines it. The reviewer didn't design or build this stage, and fixed nothing.

Status: **Open: 1 high finding (H1, narrowed)** · First review: `6cc5f8c`..`b4d99a8` · Re-check: `0d8ead1`..`c20d53f` · Reviewer: Opus, fresh session · Date: 2026-11-09

## Verdict

**Stage 1 still can't close.** On re-check, 14 of the 15 findings are fixed, and L4's pager routing needs the owner's acceptance. One high finding stays open: **H1 is only partly fixed**. A lock timeout or unknown commit in transaction B now answers 202. But any other failure there, such as a dropped connection, still answers 500 after the PSP has captured or refunded. [D28](../decisions.md#d28) names a dropped connection as unknown in so many words. The new reviewer test R8 shows it, for capture and for refund. The re-check also records two new low findings, N1 and N2. As in the first review, nothing reviewed shows wrong money, a double charge, a lost ledger row or data shared between merchants.

| Severity | First review | Fixed | Open | Accepted or pending owner |
|---|---:|---:|---:|---:|
| Critical | 0 | 0 | 0 | 0 |
| High | 2 | 1 (H2) | 1 (H1, narrowed) | 0 |
| Medium | 7 | 7 | 0 | 0 |
| Low | 6 + 2 new | 5 | 2 new (N1, N2) | 1 (L4 routing) |

Findings go back through the gates: the builder fixes each with a failing test first, a design session handles any design question, and the reviewer then re-checks only those findings.

## Re-check of the fixes (2026-11-09, evening)

**Build and tests, from a clean worktree of `c20d53f`:** `dotnet build OpenPayments.slnx -warnaserror` gave 0 warnings and 0 errors. `dotnet test OpenPayments.slnx` passed 385 tests: Domain 70, analyzers 1, mock PSP 20, checks 24, console 124, integration 40, console browser 34, acceptance 72. That matches the progress log. `tests/OpenPayments.Acceptance.Tests/Review/ReviewerStage1Tests.cs` is byte-identical to the reviewer's file. R1-R7 all pass.

| Finding | Status | What the reviewer checked |
|---|---|---|
| H1 | **Open, narrowed** | `AfterPspAsync` (`PaymentFlow.cs`) now wraps transaction B on capture, void and refund. It catches only `BusyException` and `CommitOutcomeUnknownException`, so R1 now answers 202. Anything else thrown in B still escapes as a 500: an `NpgsqlException` from a dropped connection, a `57014` statement timeout, or a failure in the `ResolveKeyAsync` that follows the commit. **R8** (new, below) throws `NpgsqlException` in transaction B: capture and refund both answer 500 after the PSP call. Authorize and sale catch every exception there (`ExecuteAttemptAsync`). The damage is now smaller than in the first review. The key stays unresolved, and after `InFlightWindow` a same-key retry renders the state rather than 409 forever. The one-live indexes and `refund_live` stop a second capture or refund under a new key. D28's "dropped connection is unknown" still doesn't hold on these routes |
| H2 | Fixed | `MockPspAdapter.SendAsync` maps `HttpRequestException` and `JsonException` to `PspUnavailableException`, which every route treats as unknown. `AdapterTransportTests` covers a reset, a malformed body and a truncated body |
| M1 | Fixed | The two-call capture runs under `"\u0001capture:" + key`. Kestrel refuses control characters in header values, so no client can send that key. The create key stores the capture's final answer (200 or 402) and stays unresolved on 202 or 503. Replaying an unresolved create whose payment is `AUTHORIZED` runs the capture again under its own key, which replays rather than calling the PSP twice. R4 passes. See N2 for one edge |
| M2 | Fixed | `CreateAttemptAsync` catches `BusyException`, answering 503 with `Retry-After: 1`. R2 passes |
| M3 | Fixed | `FromAppliedAsync` and `FromRefundAppliedAsync` store no answer for a 202, so `resolved_at` stays null. R3 and `M3_refund_timeout_…` pass |
| M4 | Fixed | `ReadKeyOnConnectionAsync` returns `Unresolved` when the operation recorded `UNKNOWN` (payment, attempt, or the key's refund) or the key is at least 10 s old. Otherwise it returns `InFlight`, answering 409 (E2). Every route renders `Unresolved` as 202 with the current payment or refund. Answers are written with `CancellationToken.None`. Capture, void and refund read the key on a refusal path, so replays of finished operations return their stored answer. Covered by `ReviewFixTests` and `PaymentStoreTests`. Crash and disconnect cases now recover through the window rule; they were read from the code, not executed |
| M5 | Fixed | On a resubmit, the amount comes from the recorded intent (`FindIntentAsync`), never from the form, so the server enforces it. `readonly` on the field is only a convenience. `Resubmit_after_busy_sends_the_recorded_amount_not_an_edited_one` passes |
| M6 | Fixed | The three API fixtures set `deadlock_timeout = 100ms` by `alter system` plus reload. `Real_deadlock_under_D1_settings_is_detected_and_retried` forces a real deadlock through `TxRunner` and passes |
| M7 | Fixed | The Task 17 line and BASELINE now say step 6's run is invalid on its count and that no valid step 6 run exists. The stale "rerun three times" is gone, and steps 2 and 4 are named as not run. The records now claim what the evidence supports |
| L1 | Fixed | `c66da95` has a line, and the counts match this re-check |
| L2 | Fixed (record) | The lab README has a D54 rerun with D1's full pair. The reviewer didn't rerun it, because the brief forbids running benchmarks. The result is plausible: deadlocks are still retried one for one, and most of a hot pair's waiters now fail fast with `55P03` |
| L3 | Fixed | The continuation logs a late success as a warning and a late failure as an error |
| L4 | Fixed, routing pending owner | `deploy/compose/prometheus/alerts.yml` has `CommitWarning` (any increase in a minute) and `AbandonedCommitsStuck` (for 1 minute). It is mounted and loaded through `rule_files`. Routing to a pager is left to Stage 5, which the owner should accept in writing |
| L5 | Fixed | Capture, void, refund and cancel enter `InFlightLimiter`. The two-call capture reuses its create's permit |
| L6 | Unchanged | The build deviations were judged acceptable. The fixes were again built by Opus under the owner's exception |

### New findings from the re-check

**N1. Low: a transaction A failure that isn't a lock or commit error still answers 500.** For example, an `NpgsqlException` from a dropped connection, in `BeginOperationAsync` or `BeginAttemptAsync`, escapes as a 500 rather than 503. It happens before any PSP call, and the transaction rolls back, so the key is free and nothing moved. It is the pre-PSP half of H1's gap, and the fix for H1 may cover both.

**N2. Low: `InFlightWindow` can be shorter than a two-call auto-capture.** The 10 s window rests on D34's 5 s request budget, but no timer enforces a total budget. A two-call create makes two PSP calls of up to 2.5 s each and up to four commits that may each wait 3 s, all under one create key whose `created_at` is transaction A's start. A same-key replay after 10 s, while that request is still running, starts a second `CaptureForCreateAsync`. The one-live-capture index stops a second PSP capture, so the worst outcome is a 409 `operation_live` to one of the two callers. No Stage 1 deployment reaches this path, because the mock supports sale. Either enforce the request budget, or derive the window from the longest path.

### Reviewer test added in the re-check

`ReviewerRecheckTests.R8_connection_loss_in_transaction_B_after_the_psp_is_not_a_500`, with cases for capture and refund. It is a copy in the session notes folder, kept out of the repository as before, and should join `tests/OpenPayments.Acceptance.Tests/Review/` unchanged. It uses the existing `BeforeOutboxInsert` failpoint, throwing `NpgsqlException` on its second call, which is transaction B. Both cases fail at `c20d53f` with 500, and should pass once H1 is fixed.

## Findings

The findings below are as first recorded; their current status is in the [re-check](#re-check-of-the-fixes-2026-11-09-evening). "R1" to "R7" are the reviewer's own tests (see [Reviewer tests](#reviewer-tests)).

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
