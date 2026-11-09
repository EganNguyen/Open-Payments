# Stage 1 Build Progress

The build gate's evidence log: one line per task, with the commit, the command that was run, and the result. Tasks 7-16 are complete under [D46](../decisions.md#d46). Task 17's laptop limit search has valid runs through step 5; step 6 is invalid from a generator limit and needs a rerun before Task 17 closes under [D53](../decisions.md#d53). Task 18 is built. No escalation is open. Escalation answers live in [Stage 1 escalations](stage-1-escalations.md), and run results in [BASELINE](../../benchmarks/BASELINE.md); this log only points to them.

Status: In progress · Last updated: 2026-11-09

| Task | Commit | Test command | Result |
|---|---|---|---|
| 7 | `840578b` | `dotnet test --filter "U1\|E11\|S1"`; `dotnet test`; `dotnet build -warnaserror`; `dotnet list OpenPayments.slnx package --vulnerable --include-transitive`; `nuget-license -i OpenPayments.slnx -t` | PASS: 12 tests (11 Money, 1 banned-API), 0 warnings, 0 vulnerable packages, licenses allowed. Probe file confirmed `RS0030` fails a `Domain` build. |
| 8 | `1dd7279` plus `dac68e8`, `c7c8a21`, `99970c0` | `dotnet test tests/OpenPayments.MockPsp.Tests`; `dotnet test`; `dotnet build -warnaserror`; k6 capacity runs | PASS: 17 mock tests; 29 total tests, 0 warnings. Capacity: valid 10k req/s preview at 1.34 ms added p99; 25k unproven on the laptop, all invalid runs recorded in `benchmarks/mock-psp-capacity.md`. |
| 9 | `f421e07` | `dotnet test tests/OpenPayments.Checks.Tests`; `dotnet test`; `dotnet build -warnaserror`; `scripts/check-migration.sh`; `op-checks --stage phase0` and `phase3` on an empty database | PASS: 17 check tests (CK1-CK10 corruption, zero rows, applicability, clean fixture), 46 tests total, 0 warnings; migration check passed on PostgreSQL 18.6; CLI wrote the result JSON and exited 1 with zero data. |
| 10 | `552a218` | `docker compose -f deploy/compose/compose.yaml up -d --wait primary pgbouncer`; `docker compose -f deploy/compose/observability.yaml up -d --wait`; `curl -sf localhost:9090/-/ready`; `docker compose config --quiet`; `dotnet test` | PASS: Prometheus ready, Grafana healthy, postgres and pgbouncer scrape targets up; API target down only until Task 14; 46 tests, 0 warnings. |
| 11 | `a24093d` | `.venv/bin/python -m pytest benchmarks/test_report.py -v`; `TARGET_OVERRIDE=mock CHECKS_DB_URL=postgres://postgres:op_local_only@127.0.0.1:25432/openpayments benchmarks/run.sh 0 --env local --stage phase0 --psp-latency 200`; `dotnet test` | PASS: 7 report tests and 46 .NET tests; Phase 0 run `20261105T103556Z-a24093d-s0-3181` wrote `checks.json` with CK1-CK3 failures on zero rows, a valid preview `result.md`, and exited 1 as required. |
| 12 | `d85a712` | `dotnet test tests/OpenPayments.Domain.Tests`; `dotnet test`; `dotnet build -warnaserror` | PASS: 70 domain tests covering U2, U3, D27, D29, D32, D33, D37, CK7 and four golden cases; 105 tests total, 0 warnings. |
| 13 | `ed1ab56` (WIP `80f0e6c`) | `dotnet test tests/OpenPayments.Integration.Tests`; `dotnet test OpenPayments.slnx`; `dotnet build OpenPayments.slnx -warnaserror` | PASS: 30 integration tests, including every C43 scenario direct and through PgBouncer 1.26.1; 135 tests total, 0 warnings. Commit wait limit keeps the backend in SyncRep with no cancel, failed/warned commits are unknown, abandoned commits complete and release their connection, and abandoned connections shed new work as `Busy`. |
| 14 | `6eb4bd3` | `dotnet test tests/OpenPayments.Acceptance.Tests`; `dotnet test OpenPayments.slnx`; `dotnet build OpenPayments.slnx -warnaserror` | PASS: create/attempt API with auth, tenant isolation, idempotency replay, in-flight 409, 100-way concurrency, fraud block, canonical log, PSP timeout 202 and simulated C43 A/B failures. 46 acceptance tests; 182 tests total at that commit, 0 warnings. |
| 15 | `6eb4bd3` plus `bbf7467` | `dotnet test --filter CaptureTests` | PASS: capture, one-call sale, two-call auto-capture, void, timeouts, C36/C50 and the completed-payment counter. 47 acceptance tests; 183 tests total at that commit, 0 warnings. |
| 16 | `6eb4bd3` plus `37b9e51`, `eced4ee` | `dotnet test --filter "RefundTests|C31a|QueryCountTests|HotQueryPlanTests"`; gitleaks; nuget-license | PASS: refunds, 30/70, timeout, shared key, C6 exact 10/40 and decline-release, E5/E16/E18, S3 query counts/generated-SQL review and S6 secret/license scans pass. C31a is complete under D46; E4 and E9 moved to C31b and were not faked. See [E2/E3](stage-1-escalations.md#e2-task-16-late-declined-callback-has-no-receiver) and [Phase 1 gate](../phase-gates/phase-1.md). |
| 17 | `2ff6232`, `6e0ece1` (previous previews `5635daa`..`2ae2c42`) | `dotnet test`; `dotnet build -warnaserror`; `python3 -m pytest benchmarks/test_report.py`; `benchmarks/resources.py` smoke | PARTIAL: D47 mix, D48 S3 checks, full `/metrics`, resource collection and CK6 steady-state logic are implemented and tested (correction round below). The laptop limit search ran on 2026-11-09 (search row below): steps 0, 1, 3 and 5 valid, step 6 invalid. Under [D53](../decisions.md#d53) Task 17 closes on a valid step 6; `C` and `N` move to the first Linux run. |
| 18 | `f680194` (C4), `37d9c6e` (C6-C11, C13), `87257fd`, `39ecee4`, `a4d0dc4` (D55) | `dotnet test OpenPayments.slnx` at `7f3d4ad` | PASS: 365 tests, 0 failed: Domain 70, analyzers 1, mock PSP 17, checks 24, integration 39, acceptance 57, console 123, console browser 34. A real-API refund from the console passes CK1 and CK3. **Open:** the C13 step 5 replica search load (`benchmarks/console-search-load.sh`) has no valid recorded run; its one run was invalidated by k6 dropped arrivals, fixed in `7dd42a7`. |

## Task 17 correction round (2026-11-06)

| Item | Commit | Command | Result |
|---|---|---|---|
| E5 mix | `2ff6232` | `dotnet test`; `node --check benchmarks/k6/mix.js`; report tests | PASS: D47 shares replace `AUTO_CAPTURE_SHARE`; `TWO_CALL_SHARE=0.2`; standalone capture does not increment completed; no new preview run because the owner paused the limit search. |
| E6 S3 | `2ff6232` | `dotnet test --filter "S3_path_counts_and_growth_are_recorded|S3_each_endpoint_within_query_count"` | PASS: measured ceilings create 29, read 4, capture 37, refund 37, sale 29, cancel 16, void 33, attempt 34, replay 2; growth checks stable at 4/37/34; transaction counts 8+15 sale, 10 cancel, 10+15 void, 11+15 attempt, 15+15 refund. |
| Metrics | `2ff6232` | `dotnet test --filter MetricsTests`; `curl :8080/metrics` | PASS: process, runtime, tx retry, conditional-update, abandoned-commit and commit-warning series are served; `/metrics` target is up. |
| Results fields | `2ff6232` | `python3 benchmarks/resources.py` smoke; report tests | PASS: resource collection reads the run window and names its Prometheus/Docker sources; outbox age and consumer lag remain n/a. No new ramp was run. |
| CK6 | `6e0ece1` | `dotnet test --filter Ck6TrendTests` | PASS: steady in-flight concurrency no longer fails; a continuing climb still does. |

## Before the limit search (2026-11-06)

The console index comparison is finished: see [the result](../../benchmarks/console-indexes.md). Two items come first, in this order:

1. Ship `refunds_payment` in its own migration, under [D50](../decisions.md#d50), and confirm with `EXPLAIN` that `Sql.LoadRefund` uses it.
2. Fix the check query that scans `outbox` once per payment, without adding an `outbox` index or weakening the check. Then measure total check time as data grows.

### Build record (2026-11-09)

**Who built these:** Opus, under an owner exception given in chat on 2026-11-09 to the rule that DeepSeek builds. The review gate must therefore be a fresh session that didn't write this code.

| Item | Commit | Command | Result |
|---|---|---|---|
| B1 | `f400b50` | `dotnet test --filter D50_load_refund_uses_refunds_payment`, before and after the migration; `dotnet test OpenPayments.slnx`; `dotnet build OpenPayments.slnx -warnaserror`; `scripts/check-migration.sh`; `scripts/migrate.sh` on the stack | PASS. **Fixtures:** all four now apply every migration, and the suite stays at 196 tests, the same as the base commit. **Plan test:** before the migration it failed with `Expected "refunds_payment", Actual null`; after, it passes. 197 tests, 0 warnings, migration check passed. **On the stack:** 312,613 payments and 41,847 refunds. `explain (analyze, buffers)` of `Sql.LoadRefund` for a payment with 14 refunds is a Bitmap Index Scan on `refunds_payment`, taking 0.042 ms. The same lookup with index scans turned off is a Seq Scan taking 1.685 ms. The `refunds` counters were reset afterwards; `seq_scan` in the next valid run goes in the B1 run line. |
| B2 | `d91cca9` | `dotnet test --filter CK4`; `dotnet test OpenPayments.slnx`; `dotnet build OpenPayments.slnx -warnaserror`; `.venv/bin/python -m pytest benchmarks/test_report.py`; `op-checks --stage phase1` on the stack | PASS. **Tests:** `CK4b` passed on the old SQL, as intended. `CK4_query_has_no_correlated_subplan` failed with `["SubPlan 1"]` and passes after the rewrite. Duration tests failed first, with a compile error in C# and 2 failures in Python. 200 tests, 0 warnings, 20 report tests. **Timing at 312,613 payments and 415,074 outbox rows:** the old `ck4.sql` hit its 300 s `statement_timeout`. The new form takes 0.353 s, with hash joins and anti-joins, no SubPlan and 0 violations. The full `op-checks` run takes 4.9 s, against more than 67 min before. Per check in ms: CK1 2,998, CK2 481, CK3 35, CK4 349, CK5 39, CK9 146, CK10 193; CK6-CK8 not applicable. All applicable checks pass. The growth step needs the reruns and is in the run lines below. |

### Laptop limit search and harness fixes (2026-11-09)

| Item | Commit | Command | Result |
|---|---|---|---|
| B1 run evidence | `f400b50` | `pg_stat_user_tables` for `refunds` after steps 0-6 | PASS: 7,801 sequential scans reading 1,193,246 rows in total, mostly while the table was tiny, against 3,158,099 index scans. Before D50, it was 1,215,455 scans reading about 19.7 billion rows at 41,847 refunds. |
| B2 growth | `d91cca9` | `duration_ms` per check for steps 3, 5 and 6 | PASS: no check's time per 100,000 payments rose more than 1.4× between 32,270 and 639,315 payments. CK1 ran at 1,230, 1,178 and 1,261 ms per 100,000 payments; CK4 at 90, 124 and 115 ms. Step 0's 399 payments are dominated by fixed overhead. No escalation is needed. |
| Error-rate gate | `4540c23` | `.venv/bin/python -m pytest benchmarks/test_report.py` | PASS, tests first: `report.py` read k6 Rate metrics by `rate`, a key only counters have, so the error-rate gate could never fail. Rejected req/s now reads `k6_requests_rejected_total`. Earlier recorded runs had value 0, so they stand. |
| E8 shares | `18d0f63` | report tests | PASS, tests first: a share is valid within max(2 points, 3 binomial standard deviations), as the owner answered under E8. |
| E8 latency | `3faaaf6` | report tests; a k6 export check | PASS, tests first: latency is judged on `http_req_duration{scenario:mix}`, without `setup()` traffic. |
| Search | `3faaaf6`..`786d8ef` | `benchmarks/run.sh 0 1 3 5 6` after a full reset | PARTIAL: steps 0, 1, 3 and 5 are valid with gates passing. Step 5 reached 248.32 completed payments/s at p99 17.74 ms. Step 6 passed its gates at 498.33 completed payments/s and p99 58.43 ms, but is invalid because k6 dropped 68 arrivals while adding VUs. The search stopped there, as E4 requires. See [BASELINE](../../benchmarks/BASELINE.md). |
| Deadlock lab | `002aba8` | `benchmarks/deadlock-lab/run.sh 5` | PASS: invariants held in all 10 rounds. Bad order gave 2,591 deadlocks and 2,591 retries, with 494 of 500 committed; ordered locking gave 0 deadlocks and 500 of 500, at 942 ms against 55,668 ms. It found [E9](stage-1-escalations.md#e9-txrunner-overrides-the-d1-lock_timeout). |
| D54 | `0056f0d` | `dotnet test` with the new in-transaction `lock_timeout` test | PASS, test first: it read `2s` before the fix and `200ms` after. Test fixtures now give `op_app` D1's `lock_timeout`; without it the C43 tests waited on locks with no limit. |
| k6 VUs | `7dd42a7` | report tests | Built: `preAllocatedVUs` equals `maxVUs`, so no VU starts mid-run. Not yet proven by a rerun of step 6. |

### Owner decisions and final runs (2026-11-09, afternoon)

| Item | Commit | Command | Result |
|---|---|---|---|
| D53-D55 | `7dd42a7` | none | The owner chose the recommended answers. Task 17 closes on laptop evidence (D53), `TxRunner` uses the role `lock_timeout` (D54), and the console accepts `acr` (D55). E2, E3 and E4 are closed for Stage 1. k6 now starts every VU before the load. |
| E9 | `0056f0d` | `dotnet test --filter D54_runner_uses_the_role_lock_timeout`; the whole suite | PASS, test first: it failed with `2s`, then read `200ms`. Fixtures now give `op_app` D1's `lock_timeout`; without it the C43 tests waited on locks with no limit. |
| E10 | `a4d0dc4` | 123 console tests; the whole suite; a real Keycloak sign-in | PASS: a password-and-TOTP sign-in through the local realm reaches Payments. Verifying it found that the OIDC handler deleted `acr` from the session; now fixed, with a test. A caught-up replica now reports zero lag. |
| C13 step 5 | `7323d59` | `benchmarks/run.sh 3` twice after a full reset; `console-search-load.sh` at 5 queries/s on standby1 | INCONCLUSIVE: both runs are invalid on the completed count, because another session rebuilt and restarted the API at 08:04 UTC, 4 minutes into the idle run. Under search load: payment p99 13.95 ms, 0 errors, replica lag at most 21.0 s, search queries at 9.6 ms average. Both meet the pass condition, but not in a valid run. |
| Step 6 rerun | `7323d59` | `benchmarks/run.sh 6` on a fresh database | FAIL: 500.03 completed payments/s, but p99 was 173.47 ms against the 100 ms gate. 0 errors, CK1-CK3 pass. API CPU 186.9%, lock waits 2,219 held, database CPU 0.67. The run is also invalid on the count, because of the same restart. Earlier, with D54 not yet applied, at 184,428 to 639,315 payments, step 6 passed its gates at p99 58.43 ms but was invalid. |
| Step 6 stall | `0056f0d` | step 6 on 1,152,650 payments | FAIL: p99 657.81 ms. A stall at 06:48:34 left 43 PSP calls `UNKNOWN`, which is correct and moved no money without a record. CK5 fails until Stage 2's reconciler resolves them. |

### Build brief (design session, 2026-11-09)

For the builder. Follow the [build gate](../delivery-workflow.md#build-gate): test first, one commit per item, one progress line per item in the correction table format above, labelled `B1` and `B2`. Nothing here changes `docs/decisions.md`, the API contract or a gate's value.

**B1: `refunds_payment` (D50)**

Today `Sql.LoadRefund` (`src/OpenPayments.Transitions/Sql.cs`) filters `refunds` on `payment_id` with no index. The test fixtures also load only `0001_initial.sql`, so a new migration would never reach the tests. Fix that first.

1. **Fixtures apply every migration.** In `tests/OpenPayments.Integration.Tests/DbFixture.cs`, `C43Fixture.cs`, `tests/OpenPayments.Checks.Tests/Fixture.cs` and `tests/OpenPayments.Acceptance.Tests/AcceptanceFixture.cs`, mount `db/migrations/` instead of the one file. Apply each `*.sql` in name order, the same order `scripts/migrate.sh` uses. Run `dotnet test` and confirm the count matches before the change: no new migration exists yet, so nothing else should move.
2. **Failing test.** Add `D50_load_refund_uses_refunds_payment` to `HotQueryPlanTests`. Seed a payment with two refunds and run `explain (format json)` on the real `Sql.LoadRefund` text, not a copy. Add `InternalsVisibleTo` for `OpenPayments.Integration.Tests` to `OpenPayments.Transitions`. Bind `@payment_id`, and set `enable_seqscan = off` on that connection first: on a table this small a sequential scan is the right plan, so the test proves the index matches the query, not the planner's choice at volume. Assert that the index name equals `refunds_payment`, using an exact match. Watch it fail: with no index on `payment_id` there is no index to find (on a two-row table the plan is a sequential scan, even with sequential scans disabled).
3. **Migration.** Create `db/migrations/0002_refunds_payment.sql` containing only `create index refunds_payment on refunds (payment_id);`, the definition in D50 and the console spec. Use a plain `create index`, because `migrate.sh` runs each file in one transaction and `concurrently` can't run there. Don't add `created_at` to the key: a payment has few refunds, so sorting them costs nothing.
4. **Checks.** Run the new test, then `dotnet test`, `dotnet build -warnaserror` and `scripts/check-migration.sh`. All must pass.
5. **Evidence at volume.** Apply the migration to the local stack with `scripts/migrate.sh`. Pick a `payment_id` that has refunds and run `explain (analyze, buffers)` of `Sql.LoadRefund`, with `enable_seqscan` left on, against a database of at least 40,000 refunds. If the stack has fewer, raise `REFUND_SEED_COUNT`, run a ramp step at the D47 mix, which issues refunds, and repeat until it reaches 40,000; record the final count. `seed-refundable.sql` only picks targets and creates no refunds. Put the plan's top node and its time in the progress line. Then reset `refunds`' counter with `select pg_stat_reset_single_table_counters('refunds'::regclass)`. In the next valid ramp run, record `seq_scan` for `refunds` from `pg_stat_user_tables`. It should be near zero, against 1,215,455 before.

**B2: CK4 set-based**

The third branch of `tools/OpenPayments.Checks/sql/ck4.sql` runs `select max(o.sequence) from outbox o where o.aggregate_id = p.id` once per payment. `outbox` has no index on `aggregate_id` and must not get one. The first two branches use `not exists`, which PostgreSQL turns into hash anti-joins, so they're fine.

1. **Coverage first.** No test covers branch 3. Add `CK4b_detects_outbox_sequence_beyond_event_seq` to `CorruptionTests`: from the clean fixture, insert an `outbox` row for the payment with `sequence = event_seq + 1`. Assert that CK4 fails at `phase1` and lists the payment. It passes on today's SQL, which is expected: it guards the rewrite.
2. **Failing test.** Add `CK4_query_has_no_correlated_subplan`: run `explain (format json)` on the `ck4.sql` text against the fixture, walk the plan, and assert that no node has `"Parent Relationship": "SubPlan"`. Watch it fail on today's SQL.
3. **Rewrite branch 3 only:**

   ```sql
   select p.id::text, 0::bigint
   from payments p
   join (select aggregate_id, max(sequence) as max_seq
         from outbox group by aggregate_id) o on o.aggregate_id = p.id
   where p.event_seq < o.max_seq;
   ```

   This returns exactly the same rows. A payment with no `outbox` rows gave a null `max` before, which never compares true, and the inner join drops it now. Branches 1 and 2 don't change.
4. **Per-check time.** Add `long DurationMs` to `CheckResult` and set it in `CheckRunner` around each check. Write it to `checks.json` (it serializes as `duration_ms`), and add a per-check table to `result.md` with the check, its result and its duration (`benchmarks/report.py`, with a test in `test_report.py`). Runs recorded before the field existed must render the duration as `n/a`. Without it, nobody can see which check slows down as data grows.
5. **Checks.** Run `dotnet test`, `dotnet build -warnaserror` and `python3 -m pytest benchmarks/test_report.py`.
6. **Evidence.** On the local stack, time the new `ck4.sql` with `explain (analyze)` on the full data. Time the old form on the same data under a `statement_timeout` (or on a smaller copy), because the old form is the per-payment scan and may not finish; record the cap if it hits. Record both times and the payment and outbox row counts. The comparison measured 0.385 s for the set-based form at 312,613 payments, against more than 67 min for the whole check run.
7. **Growth.** During the reruns of steps 0, 1 and 3 in the [run order](../benchmarks/open-experiments.md#run-order), record each check's `DurationMs` and the payment count for each run. **Escalate,** naming the check and attaching its plan, if any check's time per 100,000 payments more than doubles between the smallest and largest database. Don't change other checks' SQL without an escalation.

**Decided here without the owner:**

- The test fixtures change to apply every migration.
- The plan test turns off `enable_seqscan`.
- `create index` isn't concurrent. That is safe only while no production data exists. A migration on a live table needs its own runner path.
- `DurationMs` is added to the check results.
- The growth threshold is 2× per 100,000 payments.

E2 and E3 were closed for this build gate by the owner on 2026-11-09 ([E2](stage-1-escalations.md#e2-task-16-late-declined-callback-has-no-receiver), [E3](stage-1-escalations.md#e3-task-16-idempotency-key-retention-has-no-implementation)).

Recorded deviations: tasks 14, 15 and 16 were committed together as `6eb4bd3`, so the repository history cannot show each test failing before its implementation; the Task 14 line also reports the final counts. `C31aEdgeCaseTests.cs` was not created initially and refund endpoints live in `Program.cs` instead of the planned `Endpoints/Refunds.cs`. Task 17 needed separate harness fixes (CK1 append handling, effect-log timing and the optional Prometheus query), so its record spans several commits rather than the one-commit rule. The C31a file was added later so `--filter C31a` selects tests.

Escalation outcomes, each recorded once in [Stage 1 escalations](stage-1-escalations.md): E1 (D28 amended, built in `ed1ab56`), E2 and E3 (moved to C31b by D46), E4 (closed for Stage 1 by D53), E5 and E6 (D47 and D48, built in `2ff6232`), E7 (`c249adc`), E8 (`18d0f63`, `3faaaf6`), E9 (D54, `0056f0d`), E10 (D55, `a4d0dc4`).
