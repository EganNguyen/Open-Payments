# Stage 1 Escalations

An entry here stops work on the named task until a design session answers it. The rules are in [Delivery workflow](../delivery-workflow.md#build-gate).

## E1: Task 13 Npgsql commit cancellation breaks D28

**State:** Answered 2026-11-05, Task 13 may resume · Raised 2026-11-05

**Task:** 13, transaction runner and store. The runner cannot implement the `COMMIT` behavior D28 requires with Npgsql 10.0.3's public API.

**What conflicts:** D28 says no cancel is ever sent during `COMMIT`; after the 3 s wait limit the connection is dropped, and a dropped connection or a server "canceling wait" warning makes the outcome unknown. Task 13 says to check Npgsql's timeout behavior first and stop if its timeout sends a cancel and closing the socket can't avoid it.

The probe found both halves of the stop condition:

- `NpgsqlTransaction.CommitAsync(CancellationToken)` with a 1 s token sent `canceling wait for synchronous replication due to user request`, and the client received a normal commit success. The row was committed locally but not replicated.
- `NpgsqlConnection.CloseAsync()`/`DisposeAsync()` during the commit threw `NpgsqlOperationInProgressException: The connection is already in state 'Executing'`. The commit stayed pending and the connection could not be dropped through the public API.
- `statement_timeout` and `CommandTimeout` do not cover the synchronous commit wait.

**Evidence:** [Npgsql commit cancellation probe](../benchmarks/npgsql-commit-cancel.md), from PostgreSQL 18.6 + Npgsql 10.0.3, with the server log lines and the two probe outputs. Reproduce by starting PostgreSQL with `synchronous_standby_names = 'ANY 1 (standby1, standby2)'` and no standby, then committing with and without a token as recorded. The earlier server observation is [commit-cancel.md](../benchmarks/spike/commit-cancel.md).

**Options seen:**

1. Provide a supported way to drop the PostgreSQL socket at the 3 s limit without a cancel, for example a driver API, a custom connection wrapper, or a connection proxy. This keeps D28 and RPO 0, and needs a design decision on the mechanism.
2. Change the driver or the commit protocol to one that can abandon the wait without a cancel. This is a design change with an owner decision.
3. Accept the cancellation and treat the server warning as unknown, with the client receiving 202 rather than the driver's success. This still sends a cancel, so it conflicts with D28's first sentence and with RPO 0; it needs an explicit owner amendment, not a builder choice.
4. Give up synchronous commit or RPO 0. This is a gate/owner decision and conflicts with the design's stated durability.

**Recommendation:** Option 1 if Npgsql or a small wrapper can drop the socket without a cancel; otherwise send the measured behavior to the owner and amend D28 before building the runner. Do not take option 3 silently.

**Impact:** Tasks 13-17 depend on the runner and store. Task 12 (pure `Decide`) is complete and committed. No Task 13 code was committed.

**Answer (design session, D28 amendment approved by the owner, 2026-11-05):** a commit can be abandoned without a cancel, by no longer waiting for it. None of the four options is needed. [D28](../decisions.md#d28) is amended and the [probe](../../benchmarks/npgsql-commit-probe/README.md) is the evidence. It also corrects the entry above: `Command Timeout` does cover `COMMIT`, and it sends a cancel.

**Required in `TxRunner`:**

1. Use a dedicated data source with `Command Timeout=0`, and fail at startup if it's anything else. Work statements stay limited by `ct` and the role's `statement_timeout`.
2. Enable TCP keepalive on that data source, so a dead peer can't hold a connection forever.
3. Call `CommitAsync(CancellationToken.None)` explicitly, then `WaitAsync(limit)` with no request or host token. Never call `NpgsqlCommand.Cancel`.
4. On timeout, throw `CommitOutcomeUnknownException` and hand the connection and transaction to a continuation. Don't dispose them while the commit runs, because dispose throws during `Executing`.
5. The continuation observes the late result, logs it, updates metrics, and only then disposes the connection. No exception goes unobserved.
6. Treat any notice with `InvariantSeverity` `WARNING` during `COMMIT` as unknown, count it and raise the page signal. Don't match on message text. Attach the handler only for the commit.
7. Treat any exception from `COMMIT` as unknown, not failure.
8. Keep waiting commits inside the bounded pool, with no second pool or queue. An exhausted pool sheds new work as Busy.
9. Add a gauge for abandoned commits still running and a counter for commit warnings to [Metrics](../metrics.md).
10. At shutdown, don't dispose connections with a commit still running.

**Configuration:** PgBouncer `query_timeout` stays 0 and Postgres `transaction_timeout` stays unset, because either could interrupt the wait. Add both to `scripts/check-db-settings.sh`.

**Tests:** the four added to [Task 13](../superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md#task-13-transaction-runner-and-store). Run the C43 tests directly and through PgBouncer.

**Escalate again if** a test shows a cancel during `COMMIT`, a success reported while no standby has confirmed and no warning arrived, an abandoned connection that isn't released after a standby returns, or a same-key retry during an abandoned commit that doesn't return Busy.

## E2: Task 16 late declined callback has no receiver

**State:** Answered 2026-11-06 ([D46](../decisions.md#d46)) · Raised 2026-11-06

**Task:** 16, refunds and the Phase 1 exit gate. C31a lists E9: a late `declined` callback on a captured payment must be ignored, logged and alerted.

**What conflicts:** The API has no callback route, no `psp_events` insertion path and no signature verification. The roadmap puts the callback receiver and parking in Stage 2. A domain-only test would prove a rule that no request can reach, so it would not close E9.

**Options seen:**

1. Defer E9 and C31a closure to Stage 2, and record the Phase 1 gate as blocked by this one case. This keeps the build honest and does not expand the API beyond its task.
2. Build the callback route, signature check, event insert and parked-event retry now. That is the Stage 2 receiver, not a Task 16 change, and needs its own design and tests.
3. Add a domain-only late-decline test and call E9 covered. This is rejected because the route does not exist.

**Recommendation:** Option 1. The owner should decide whether the Phase 1 gate may close with E9 deferred to Stage 2, or whether the callback receiver moves into Phase 1.

**Impact:** C31a remains incomplete. No callback route or fake test was added.

**Answer (design session, approved by the owner, 2026-11-06):** Option 1, recorded as a scope correction rather than a Phase 1 waiver. E9 moves from C31a to C31b ([D46](../decisions.md#d46)). A late callback can't happen in Phase 1 because no callback route exists. Stage 2 builds the receiver, and C9 already covers its callbacks. Do not add a route or a domain-only test in Stage 1.

C31b's E9 test must send a signed late `declined` callback for a captured payment through the real route, and assert:

- Payment state and ledger entries are unchanged.
- The event is stored in `psp_events`.
- The event is logged and the ignored-event counter rises.
- The FS-05 contradiction alert fires.

## E3: Task 16 idempotency-key retention has no implementation

**State:** Answered 2026-11-06 ([D46](../decisions.md#d46)) · Raised 2026-11-06

**Task:** 16, refunds and the Phase 1 exit gate. E4 says a key reused after the retention period is treated as new, with at least 24 hours after resolution and never while unresolved ([D19](../decisions.md#d19)).

**What conflicts:** No Stage 1 task defines a key-expiry mechanism, and `src/` contains no job or route that deletes or ages `idempotency_keys`. The schema has `resolved_at` and the partial `idempotency_keys_expirable` index, but nothing reads them. Adding expiry silently would create a retention policy and a scheduler that are not in the plan.

**Options seen:**

1. Defer key expiry to Stage 2 with the reconciler/timer work, and record E4 as not covered in the Phase 1 C31a suite.
2. Add a Stage 1 expiry job now, with a configurable retention of at least 24 hours, a guard that never deletes `resolved_at is null`, and metrics/alerts. This is new scope and needs an owner-approved design.
3. Expire keys lazily on read. This still needs an age policy and does not handle storage growth; it also risks deleting a key while an unresolved operation is still findable.

**Recommendation:** Option 1 unless the owner wants the expiry job in Stage 1. Do not mark E4 covered without an executable mechanism.

**Impact:** C31a E4 remains open. No retention code or test was added.

**Answer (design session, approved by the owner, 2026-11-06):** Option 1. E4 moves from C31a to C31b ([D46](../decisions.md#d46)), next to E24 and C41, which already expect the key expiry job in Phase 2. Do not add expiry in Stage 1, and Option 3, lazy expiry on read, is rejected. [D19](../decisions.md#d19) is unchanged: retention is at least 24 hours after resolution, and a key is never expired while its operation is unresolved. Until the job exists, keys are simply kept, which is the safe direction: a late retry replays its answer and can't charge twice.

Stage 2's design gate must:

- Measure V7 (batched delete under load) and record the expiry method before the job is built.
- Check that every path that resolves an operation sets `resolved_at`, including `UNKNOWN` resolved by the reconciler.

C31b's E4 test must run the real job and assert:

- A resolved key past retention is removed, and reusing it creates a new operation.
- A resolved key just inside retention still replays with no PSP call.
- A key with `resolved_at is null` is kept, whatever its age.

## E4: Task 17 has no Linux host for steps 4-5 (escalation E4)

**State:** Open, no host yet; laptop previews continue under the answer below · Raised 2026-11-06

**Task:** 17, the first ramp. Steps 0-3 are local previews; steps 4-5 and the first `C` require a Linux host. This is escalation E4; edge case E4 is the key-retention case moved to C31b by [D46](../decisions.md#d46).

**What blocks:** The available machine is the owner's MacBook. The playbook and Task 17 both say laptop numbers are comparative only and cannot answer G1. The host must provide all of the following before steps 4-5 can produce a trustworthy `C`:

- The mock PSP repeated at 25,000 req/s on benchmark-class hardware, with less than 5 ms added p99. Task 8 Step 5 is still unproven; the laptop's valid 10,000 req/s preview is not enough.
- A measured `MOCK_ADDED_P99_MS` for the run, not a default of zero.
- Load generators on separate machines from the system under test.
- NVMe storage, with WAL on its own volume.
- Quorum replication across availability zones.
- Data prefilled to 100M payments before the run.

A Phase 1 Linux run is still labelled **preview** under the trust rules, because `Recorded` starts at Phase 3. That label does not make a laptop number acceptable as `C`.

**Options seen:**

1. Provide a Linux host meeting the list above for Task 17 steps 4-5, with the generator separate from the system under test. This is the only option that produces a real `C`.
2. Mark Task 17 partial and leave `C` unset; carry the Linux run into the review gate or Stage 2 preparation. This keeps the evidence honest but does not close Task 17.
3. Use the MacBook number as `C`. Rejected: it is a laptop preview and the baseline says so.

**Recommendation:** Option 1. Until a host exists, keep Task 17 PARTIAL and do not report the build as ready for review.

**Impact:** Task 17 steps 4-5 and `BASELINE.md`'s capacity section are incomplete. The Phase 1 gate stays open. The local steps 0-3 are valid previews only and are recorded in [BASELINE](../../benchmarks/BASELINE.md).

**Answer (owner, 2026-11-06):** Option 2 for now. There is no Linux host, so Task 17 stays PARTIAL and `C` stays unset. The laptop may be used to find bottlenecks, under the rules below. This entry stays open until a host exists.

**Do first, in this order:**

1. Make the [E5](#e5-task-17-workload-mix-has-no-agreed-saletwo-call-shares) mix change and the [E6](#e6-s3-query-count-budgets-have-no-design-source) S3 test change.
2. Export the metrics [Metrics](../metrics.md) already promises and `/metrics` does not serve: `process_cpu_seconds_total`, `process_resident_memory_bytes`, `process_num_threads`, ThreadPool queue, Gen 2 GC and allocation rate, plus `op_tx_retries_total` and `op_conditional_update_zero_rows_total`.
3. Export the abandoned-commit gauge and the commit-warning counter that [E1](#e1-task-13-npgsql-commit-cancellation-breaks-d28) item 9 requires, and add both to Metrics. `TxRunner` tracks them today, but they are neither served nor documented.
4. Fill the `n/a` fields in `result.md` from the run's own time window. Use `pgbouncer_pools_client_maxwait_seconds` for pool wait, `pg_stat_database_deadlocks` for deadlocks and `pg_wal_bytes` for WAL rate. Add lock-wait and database and container CPU sources that work on the local stack, and name each source in the [benchmark README](../../benchmarks/README.md). Outbox age and consumer lag stay `n/a` until their phases.

**Laptop limit search:**

1. Rerun steps 0-3 at the new mix, then run steps 4, 5, 6 and 7 in turn, with `--env local`.
2. Stop at the first failed gate or invalid run. An invalid run, such as generator CPU over 80% or dropped arrivals, shows the laptop's limit, not the system's. Say so, and don't call it a system bottleneck.
3. Record each run in [BASELINE](../../benchmarks/BASELINE.md), with the database size. Before naming a bottleneck, the resource fields must be filled, and the entry must cite the numbers behind it.

**Improving from what it finds:**

1. Change one thing at a time, and name the bottleneck it targets before changing it.
2. Compare before and after at the same step, mix, configuration and database size, starting from the same database state. Keep the change only if the measured limit improves and the applicable checks still pass.
3. Never disable quorum replication, durable flushes, correctness checks or a gate to reach a higher rate.
4. Escalate before any change that touches `docs/decisions.md`, the API contract, a migration beyond the current task, or a gate's value. Batching store statements is allowed only after a recorded bottleneck, as [D48](../decisions.md#d48) says.
5. Laptop pool sizes and tuning values are not production settings. They go into the production estimate only after a Linux run.

**Not established by this work:** `C`, `N`, whether 5,000 completed payments/s is reachable, and the close of Task 17. The review gate stays closed to Task 17 until a host exists, or until the owner decides otherwise.

**Escalate again if** the limit search fails below step 4 at the new mix, or if a bottleneck can only be fixed by a design change.

## E5: Task 17 workload mix has no agreed sale/two-call shares

**State:** Answered 2026-11-06 ([D47](../decisions.md#d47)), builder work open · Raised 2026-11-06

**Task:** 17 and the benchmark mix. The plan says "60% create and pay, with a configured share of sale and two-call capture", but the checked-in `mix.js` sends only create and authorise/sale; it never sends a standalone capture. The design session must say what share of the 60% is one-call sale and what share is authorize-then-capture.

**What conflicts:** The target `T` depends on the mix: about 8,300 req/s if each payment is a one-call sale, or about 16,700 if every payment needs a separate capture. The existing local previews use neither stated split. Choosing 50% auto capture in `AUTO_CAPTURE_SHARE` is not the same as specifying the share of two-call capture, and it does not add capture requests.

**Options seen:**

1. Design session supplies the intended share of one-call sale and two-call capture, and the matching `T` (8,300 or 16,700 or a measured combination).
2. Use only one-call sale and set `T` to 8,300. This is a design choice, not a builder default.
3. Add two-call capture for a stated share and recompute `T` from the measured commits per payment.

**Recommendation:** Option 1. Do not report `C` or `T` until the design session answers; `C` and `T` must come from the same mix.

**Impact:** Task 17's local previews remain comparative. The existing mix understates the manual path and cannot support the capacity target.

**Answer (design session, approved by the owner, 2026-11-06):** a variant of Option 3. 20% of payments are two-call, as an assumption until real merchant data exists, and `T` is 10,000 req/s ([D47](../decisions.md#d47)). "Two-call" here means two requests: a merchant authorizes, then sends a separate capture.

| Request | Share of all requests |
|---|---:|
| One-call sale | 40% |
| Authorize | 10% |
| Standalone capture | 10% |
| Status | 15% |
| Refund | 10% |
| Retry | 10% |
| Other | 5% |

| Two-call share | `T` (req/s) | Commits/s at target | Active cells needed |
|---:|---:|---:|---:|
| 0% | 8,333 | 17,500 | 4 |
| **20%** | **10,000** | **21,000** | **5** |
| 50% | 12,500 | 26,250 | 6 |
| 100% | 16,667 | 35,000 | 8 |

These are estimates, with callbacks at their upper limit. Active cells are the number that keeps each cell under the 5,500 commits/s check in [Production Deployment](../architecture/prd-deployment.md#what-must-be-validated), after adding its 25% allowance for writes the spike leaves out.

**Required from the builder:**

1. Replace `AUTO_CAPTURE_SHARE` with `TWO_CALL_SHARE`, default 0.2, and record it in `config.json`. Update `benchmarks/README.md`.
2. In `mix.js`, roll the shares above. A capture takes a payment from a per-VU queue of authorized payments. If the queue is empty, send an authorize instead and count the substitution. A declined or canceled payment leaves the queue.
3. Report the shares achieved. A run is invalid if any share misses its target by more than 2 percentage points.
4. In `steps.json`, set steps 9 and 10 to 10,000 req/s and step 11 to 20,000. Steps 9 and 10 are still judged on completed payments/s.
5. Add a test that a standalone capture does not raise the completed-payment count.
6. Rerun the local preview steps 0-3 at the new mix and update [BASELINE](../../benchmarks/BASELINE.md). Earlier runs stay on record as comparative only, under their old mix.
7. Report `C` and `N` only at this mix. Compare measured commits per payment with 2.8, since Phase 1 has no callbacks.

The mock's 25,000 req/s floor is unchanged. The mix needs about 7,000 PSP calls/s at target, so the floor is more than three times that.

**Escalate again if** the achieved shares can't stay within the limit, or if a capture needs to count as a completed payment for the numbers to reconcile.

## E6: S3 query-count budgets have no design source

**State:** Answered 2026-11-06 ([D48](../decisions.md#d48)), builder work complete in `2ff6232` · Raised 2026-11-06

**Task:** 16, S3 endpoint query counts. The current test uses create ≤40, read ≤10, capture ≤40 and refund ≤50 commands. Those numbers were chosen by the builder; a search of the design and verification documents found no equivalent budget.

**What is measured:** On the acceptance fixture the actual counts are create 29, read 4, capture 37 and refund 37 commands. The test prints these and still enforces the builder-chosen ceilings. A request that touches one payment cannot demonstrate an N+1 pattern, so even an exact count is not a substitute for a design budget or a bounded query plan.

**Options seen:**

1. The design session sets per-endpoint command budgets or a query-plan rule, and S3 tests against it.
2. Keep the current builder-chosen ceilings as provisional limits, mark S3 PARTIAL, and revisit at the review gate. This does not make them design values.
3. Remove the budgets and record only the actual counts. This would make regressions invisible.

**Recommendation:** Option 1. Until a design number exists, keep S3 PARTIAL and do not mark it settled or use it to close Task 16.

**Impact:** S3 remains open evidence. No gate value or test was weakened; the test now records the actual counts.

**Answer (design session, approved by the owner, 2026-11-06):** Option 1, with the budget set by rule ([D48](../decisions.md#d48)). One correction to the entry above: two design sources do apply. The [engineering standards](../engineering-standards.md#database) require tests that catch N+1 patterns, and the plan's global constraints said "at most about 5 statements per transaction". The current test checks neither.

**Required from the builder:**

1. **No growth:** assert an endpoint's command count is the same with few and with many related rows. At least: a refund with 1 and with 10 prior refunds, a read of those same payments, and an attempt with 1 and with the most prior attempts the cap allows.
2. **Per-path ceilings:** replace the 40/10/40/50 limits with measured counts. Start from manual create 29, read 4, capture 37 and refund 37, re-measured on the final code. Add one-call sale, void, cancel, attempts and idempotent replay, which are not measured today.
3. **Per-transaction counts:** record statements and round trips for each transaction on those paths, and set ceilings from the measurements.
4. Lowering a ceiling needs no approval. Raising one is an escalation with the reason and the commands added.

Do not rewrite the store or add batching in Stage 1. Reading the code suggests the apply transaction runs about 12-14 commands, which is not measured yet. Step 3 records the real number.

S3 stays PARTIAL until these pass. Revisit the statement count after Linux step 5: if pool wait or lock waits limit capacity, batching is the first fix to try.

**Escalate again if** a command count grows with related rows, or a transaction's count varies between runs of the same path for reasons other than a documented retry.

## E7: benchmark reset leaves the CK1 ledger manifest stale

**State:** Open · Raised 2026-11-06

**Task:** 17, validating a preview after a database reset. This is a harness reset issue, not a money defect.

**What happened:** Run `20261106T074406Z-095f2b2-s2-3717` (step 2, commit `095f2b2`) was valid, kept every D47 share within the 2-point limit and passed CK2-CK6, CK9 and CK10. CK1 failed with one `ledger_manifest` violation.

**Evidence:** The reset truncated the payment, attempt, refund, ledger, outbox, idempotency, event and settlement tables, but not the checks' private `ck.ledger_rows` manifest. Before the run it held 148,646 rows; the new ledger had 25,890 rows. 122,756 manifest rows had no matching ledger entry, so `LedgerRowsChangedAsync` correctly reported deletions. `CheckRunner.InitializeAsync` truncates `ck.psp_effects` for each run but deliberately leaves `ck.ledger_rows` to detect tampering within a run. The reset did not clear it between independent runs.

**Why it matters:** CK1 is doing what it was built to do. A benchmark reset is a controlled deletion, but CK1 cannot distinguish that from an operator deleting ledger rows while a run is in progress unless the reset also clears the manifest between separate runs.

**Options seen:**

1. Clear `ck.ledger_rows` while the API and mock are stopped, as part of a full benchmark reset, and document it next to the effect-log reset. Rerun step 2 and keep CK1 unchanged inside a run.
2. Drop and recreate the `ck` schema on reset. This has the same effect, but removes any other check state as well.
3. Leave the manifest and treat the CK1 failure as a false positive. Rejected: it leaves the run failed and hides the reset requirement.

**Recommendation:** Option 1. Clear the manifest only between independent runs, never while a run is in progress, and rerun step 2 before continuing the limit search. Do not weaken CK1.

**Impact:** No passing step-2 preview is recorded. Task 17 remains PARTIAL and the limit search stays stopped at the first failed gate. The run is still evidence that the mix fix and the k6 metric-name fix work: sale 40.10%, authorize 10.38%, capture 9.51%, status 15.16%, refund 9.98%, retry 10.05%, other 4.83%, 56 capture substitutions, and no k6 unique-series warning.

**Escalate again if** clearing the manifest does not make CK1 pass on a rerun, or if any share leaves the 2-point limit.
