# Stage 1 Build Progress

The build gate's evidence log: one line per task, with the commit, the command that was run, and the result. Tasks 7-16 are complete under [D46](../decisions.md#d46). Task 17 is PARTIAL: the D47 mix and metric work is in place, the laptop limit search waits for the two items under "Before the limit search", and Linux steps 4-5 remain open. Task 18 is not started and waits for the [console design gate](stage-c-design.md).

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
| 17 | `2ff6232`, `6e0ece1` (previous previews `5635daa`..`2ae2c42`) | `dotnet test`; `dotnet build -warnaserror`; `python3 -m pytest benchmarks/test_report.py`; `benchmarks/resources.py` smoke | PARTIAL: D47 mix (40/10/10/15/10/10/5, per-VU capture queue, 2-point share validity), D48 S3 checks, full `/metrics`, resource collection and CK6 steady-state logic are implemented and tested. The previous previews are superseded by the mix change. The laptop limit search hasn't started. Valid step 2 and step 4 runs at the new mix exist, and the only ones recorded are in [the index comparison](../../benchmarks/console-indexes.md). No `C` or `N`; Linux steps 4-5 remain open ([E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4)). |

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

### Build brief (design session, 2026-11-09)

For the builder. Follow the [build gate](../delivery-workflow.md#build-gate): test first, one commit per item, one progress line per item in the correction table format above, labelled `B1` and `B2`. Nothing here changes `docs/decisions.md`, the API contract or a gate's value.

**B1: `refunds_payment` (D50)**

Today `Sql.LoadRefund` (`src/OpenPayments.Transitions/Sql.cs`) filters `refunds` on `payment_id` with no index. The test fixtures also load only `0001_initial.sql`, so a new migration would never reach the tests. Fix that first.

1. **Fixtures apply every migration.** In `tests/OpenPayments.Integration.Tests/DbFixture.cs`, `C43Fixture.cs` and `tests/OpenPayments.Checks.Tests/Fixture.cs`, mount `db/migrations/` instead of the one file. Apply each `*.sql` in name order, the same order `scripts/migrate.sh` uses. Run `dotnet test` and confirm the count matches before the change: no new migration exists yet, so nothing else should move.
2. **Failing test.** Add `D50_load_refund_uses_refunds_payment` to `HotQueryPlanTests`. Seed a payment with two refunds and run `explain (format json)` on the real `Sql.LoadRefund` text, not a copy. Add `InternalsVisibleTo` for `OpenPayments.Integration.Tests` to `OpenPayments.Transitions`. Bind `@payment_id`, and set `enable_seqscan = off` on that connection first: on a table this small a sequential scan is the right plan, so the test proves the index matches the query, not the planner's choice at volume. Assert that the index name equals `refunds_payment`, using an exact match. Watch it fail. It should find no index or `refunds_pkey`.
3. **Migration.** Create `db/migrations/0002_refunds_payment.sql` containing only `create index refunds_payment on refunds (payment_id);`, the definition in D50 and the console spec. Use a plain `create index`, because `migrate.sh` runs each file in one transaction and `concurrently` can't run there. Don't add `created_at` to the key: a payment has few refunds, so sorting them costs nothing.
4. **Checks.** Run the new test, then `dotnet test`, `dotnet build -warnaserror` and `scripts/check-migration.sh`. All must pass.
5. **Evidence at volume.** Apply the migration to the local stack with `scripts/migrate.sh`. Pick a `payment_id` that has refunds and run `explain (analyze, buffers)` of `Sql.LoadRefund`, with `enable_seqscan` left on, against a database of at least 40,000 refunds. If the stack has fewer, run a ramp step at the D47 mix first, which issues refunds, and say so. `seed-refundable.sql` only picks targets and creates no refunds. Put the plan's top node and its time in the progress line. Then reset `refunds`' counter with `select pg_stat_reset_single_table_counters('refunds'::regclass)`. In the next valid ramp run, record `seq_scan` for `refunds` from `pg_stat_user_tables`. It should be near zero, against 1,215,455 before.

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
4. **Per-check time.** Add `long DurationMs` to `CheckResult` and set it in `CheckRunner` around each check. Write it to `checks.json`, and show it as a column in `result.md`'s check table (`benchmarks/report.py`, with a test in `test_report.py`). Without it, nobody can see which check slows down as data grows.
5. **Checks.** Run `dotnet test`, `dotnet build -warnaserror` and `python3 -m pytest benchmarks/test_report.py`.
6. **Evidence.** On the local stack, time the old and new `ck4.sql` with `explain (analyze)` on the same data. Record both times and the payment and outbox row counts. The comparison measured 0.385 s for the set-based form at 312,613 payments, against more than 67 min for the whole check run.
7. **Growth.** During the reruns of steps 0, 1 and 3 in the [run order](../benchmarks/open-experiments.md#run-order), record each check's `DurationMs` and the payment count for each run. **Escalate,** naming the check and attaching its plan, if any check's time per 100,000 payments more than doubles between the smallest and largest database. Don't change other checks' SQL without an escalation.

**Decided here without the owner:**

- The test fixtures change to apply every migration.
- The plan test turns off `enable_seqscan`.
- `create index` isn't concurrent. That is safe only while no production data exists. A migration on a live table needs its own runner path.
- `DurationMs` is added to the check results.
- The growth threshold is 2× per 100,000 payments.

Owner instruction (2026-11-06): E2 and E3 remain open for the build gate. D46's scope correction is recorded and accepted, but the build is not review-ready while E2 or E3 is open.

Recorded deviations: tasks 14, 15 and 16 were committed together as `6eb4bd3`, so the repository history cannot show each test failing before its implementation; the Task 14 line also reports the final counts. `C31aEdgeCaseTests.cs` was not created initially and refund endpoints live in `Program.cs` instead of the planned `Endpoints/Refunds.cs`. Task 17 needed separate harness fixes (CK1 append handling, effect-log timing and the optional Prometheus query), so its record spans several commits rather than the one-commit rule. The C31a file was added later so `--filter C31a` selects tests.

D46 outcome (2026-11-06): E4 and E9 moved from C31a to C31b with owner approval, so Task 16 is complete without a callback receiver or key-expiry job. No test was faked. See [D46](../decisions.md#d46) and [E2/E3](stage-1-escalations.md#e2-task-16-late-declined-callback-has-no-receiver).

E4 outcome (2026-11-06): no Linux host for now, so Task 17 stays PARTIAL. The next builder work and the rules for a laptop limit search are in [E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4). The E1 abandoned-commit gauge and commit-warning counter, and the Task 14 runtime and process metrics, are now served by `/metrics` (commit `2ff6232`).

E5 and E6 outcome (2026-11-06): the owner approved [D47](../decisions.md#d47) and [D48](../decisions.md#d48). The D47 mix and D48 S3 checks are implemented in `2ff6232`; S3 has measured ceilings, growth checks and per-transaction counts. Task 17 still needs a run at the new mix, which is paused for the console index comparison. See [E5](stage-1-escalations.md#e5-task-17-workload-mix-has-no-agreed-saletwo-call-shares) and [E6](stage-1-escalations.md#e6-s3-query-count-budgets-have-no-design-source).

E1 outcome (2026-11-06): the amended D28 is implemented and covered by the C43 suite on PostgreSQL 18.6, both directly and through PgBouncer 1.26.1. `CommitAsync(CancellationToken.None)` plus the runner's 3 s wait limit never sends a cancel; a timeout, dropped connection, or warning becomes `CommitOutcomeUnknownException`. The commit keeps its connection until the standby confirms, and the abandoned-commit gauge returns to zero when it does. Recorded in [E1](stage-1-escalations.md#e1-task-13-npgsql-commit-cancellation-breaks-d28).
