# Phase 1 exit record

Status: **not achieved** · Last updated: 2026-11-06

The build and its tests pass, but the Phase 1 checklist is not complete. The remaining gaps
are the independent S5 review, S3's query rules ([D48](../decisions.md#d48)), the benchmark mix
([D47](../decisions.md#d47)) and Task 17's Linux capacity run; the gate stays open until they
are closed. C31a's scope was corrected by [D46](../decisions.md#d46): E4 and E9 now belong to
C31b in Phase 2, so C31a is complete without a fake callback route or expiry job.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 12 projects, 0 warnings. |
| `dotnet test OpenPayments.slnx` | PASS: 196 tests. Domain 70, analyzers 1, mock PSP 17, checks 21, integration 31, acceptance 56. |
| `dotnet list OpenPayments.slnx package --vulnerable --include-transitive` | PASS: no vulnerable packages from the configured source. |
| `scripts/check-db-settings.sh` | PASS against the running stack: quorum standbys 2, `transaction_timeout` unset, PgBouncer `query_timeout=0`, and the application timeouts direct and through PgBouncer. |
| License scan | PASS: `nuget-license` 4.0.18 is pinned in `.config/dotnet-tools.json`; `dotnet tool restore` and `dotnet tool run nuget-license -i OpenPayments.slnx -t` exited 0. |

## Checklist

| ID | Result |
|---|---|
| U1 | PASS: money property tests. |
| U2 | PASS: transition and state-search tests. |
| U3 | PASS: ledger balance/property tests and the database append-only grant test. |
| U4 | PASS: same-key replay, different-hash 422 and in-flight/race integration tests. |
| C5 | PASS: 100 concurrent create requests with one key produced one payment, one PSP call and semantically identical answers. |
| C6 | PASS: all-approved 50-way run retried transient conflicts to exactly 10 successes and 40 rejections; refunded plus pending equals captured. A forced decline releases the reservation and allows the full amount to be refunded again. |
| C31a | PASS: E1-E3, E5-E8, E11-E13, E16-E18, E21 and E25-E29 have API-level coverage. E4 and E9 moved to C31b by [D46](../decisions.md#d46); no callback route or expiry job was added and no test was faked. |
| C43 | PASS: all eight runner scenarios directly and through PgBouncer 1.26.1, on PostgreSQL 18.6. |
| S1 | PASS: the solution builds with the banned-API analyzer; the analyzer probe test passes. |
| S3 | PASS under [D48](../decisions.md#d48): `EXPLAIN` confirms the merchant/created index. Measured ceilings are create 29, read 4, capture 37, refund 37, sale 29, cancel 16, void 33, attempt 34 and replay 2 Npgsql commands. Read/refund stay 4/37 after 1 or 10 prior refunds; attempt stays 34 after 9 prior attempts. Transactions record 8+15 statements for sale, 10 cancel, 10+15 void, 11+15 attempt, 15+15 refund; round trips equal statements. Review: 29 hand-written commands, 113 bound parameters, no `select *`, no string-built SQL and no EF/DbContext tracking. |
| Metrics | PASS: `/metrics` serves the four Task 14 metrics, `op_psp_wait_seconds`, `op_inflight`, process/runtime series, `op_tx_retries_total`, `op_conditional_update_zero_rows_total`, `op_abandoned_commits` and `op_commit_warnings_total`. Prometheus reports the `api` target up. |
| S5 | PARTIAL: analyzers are errors and the request-path projects have the banned-symbol file. The review checklist was run on 2026-11-06: no `Task.Run`, `.Result`, `.Wait()` or `GetAwaiter().GetResult()` in `src/`; no `lock` spans an `await`; only bounded `Task.WhenAll` over the abandoned-commit list; no `DbContext`. The independent review is still required at the review gate. |
| S6 | PASS: `dotnet list package --vulnerable --include-transitive` found no vulnerable packages. `nuget-license` 4.0.18 (Apache-2.0) is pinned in `.config/dotnet-tools.json`; `dotnet tool restore` and `dotnet tool run nuget-license -i OpenPayments.slnx -t` exited 0. `gitleaks 8.30.1 dir .` and `gitleaks git .` both exited 0 with no leaks across the working tree and git history. |

## Named gaps

- S5: independent review at the review gate; the self-review recorded above is not a substitute.
- Benchmark mix: the D47 mix and share-validity checks are implemented. The previous steps 0-3
  are superseded by the mix change; no run at the new mix is recorded because the owner paused
  the laptop limit search for the console index comparison.
- Laptop only for now (owner, 2026-11-06): the E4 answer sets the rules for a laptop limit
  search. Its results stay previews and can't close Task 17.
- Task 17: steps 4-5 need a Linux host and remain unrun. No `C` or `N` is recorded. See
  [E4](../gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4).
- E2 and E3 remain open by owner instruction; the build is not review-ready.
- C31a has no open design gap after D46; edge case E4 and E9 are C31b tests, not Stage 1 evidence.

## Task 17 local preparation and preview

On 2026-11-06 the stale local volumes were inspected and recreated. The primary volume had
tables but no `schema_migrations` rows, and its old replication slots made `pg_basebackup` fail.
After recreating all three volumes, the stack started with `PG_CONFIG=primary.macbook.conf` on
the primary and both standbys. Both standbys reached `quorum`; `scripts/migrate.sh` applied
`0001_initial`; and `scripts/check-db-settings.sh` passed. The API and mock PSP were added to
`deploy/compose/compose.yaml`, with the mock effect log mounted so CK2 can see this run's
effects.

The harness exposed three bugs while running the preview. CK1 treated appended ledger rows as
tampering, so its aggregate manifest was replaced with per-row hashes that flag only changed
or deleted rows. CK2 read the effect log before the run; it now reads it after the drain. A
failed optional Prometheus query no longer aborts the report.

The earlier steps 0-3 used the old 60/15/10/10/5 mix and are superseded. The D47 mix,
share validity, S3 checks, full metric contract and resource collection are committed; the
owner paused the laptop limit search before a run at the new mix. No laptop number is reported
as `C`. Linux steps 4-5 remain open ([E4](../gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4)).
