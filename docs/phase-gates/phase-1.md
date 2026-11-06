# Phase 1 exit record

Status: **not achieved** · Last updated: 2026-11-06

The build and its tests pass, but the Phase 1 checklist is not complete. The remaining gaps
are the independent S5 review and Task 17's Linux capacity run; the gate stays open until they
are closed. C31a's scope was corrected by [D46](../decisions.md#d46): E4 and E9 now belong to
C31b in Phase 2, so C31a is complete without a fake callback route or expiry job.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 12 projects, 0 warnings. |
| `dotnet test OpenPayments.slnx` | PASS: 189 tests. Domain 70, analyzers 1, mock PSP 17, checks 18, integration 31, acceptance 52. |
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
| S3 | PASS: `EXPLAIN` confirms the merchant/created index. `S3_each_endpoint_within_query_count` counts Npgsql commands for create (≤40), read (≤10), capture (≤40) and refund (≤50). Review: 29 hand-written commands, 113 bound parameters, no `select *`, no string-built SQL and no EF/DbContext tracking. |
| S5 | PARTIAL: analyzers are errors and the request-path projects have the banned-symbol file. The review checklist was run on 2026-11-06: no `Task.Run`, `.Result`, `.Wait()` or `GetAwaiter().GetResult()` in `src/`; no `lock` spans an `await`; only bounded `Task.WhenAll` over the abandoned-commit list; no `DbContext`. The independent review is still required at the review gate. |
| S6 | PASS: `dotnet list package --vulnerable --include-transitive` found no vulnerable packages. `nuget-license` 4.0.18 (Apache-2.0) is pinned in `.config/dotnet-tools.json`; `dotnet tool restore` and `dotnet tool run nuget-license -i OpenPayments.slnx -t` exited 0. `gitleaks 8.30.1 dir .` and `gitleaks git .` both exited 0 with no leaks across the working tree and git history. |

## Named gaps

- S5: independent review at the review gate; the self-review recorded above is not a substitute.
- Task 17: the local preview steps 0-3 pass, but steps 4-5 need a Linux host. No `C` or `N` is
  recorded. See [E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5).
- C31a has no open design gap after D46; E4 and E9 are C31b tests, not Stage 1 evidence.

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

Steps 0-3 are valid previews under the gates: CK1-CK5, CK9 and CK10 pass, and CK6/CK7/CK8
are not applicable. Their run IDs and numbers are in [BASELINE](../../benchmarks/BASELINE.md).
No laptop number is reported as `C`. Linux steps 4-5 are open ([E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5)).
