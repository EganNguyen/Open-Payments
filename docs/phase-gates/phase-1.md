# Phase 1 exit record

Status: **not achieved** · Last updated: 2026-11-06

The build and its tests pass, but the Phase 1 checklist is not complete. The gaps are listed
by name below; the gate stays open until they are closed.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 12 projects, 0 warnings. |
| `dotnet test OpenPayments.slnx` | PASS: 188 tests. Domain 70, analyzers 1, mock PSP 17, checks 17, integration 31, acceptance 52. |
| `dotnet list OpenPayments.slnx package --vulnerable --include-transitive` | PASS: no vulnerable packages from the configured source. |
| `bash -n scripts/check-db-settings.sh` | PASS. The script now checks `transaction_timeout` is unset and PgBouncer `query_timeout=0`; it was not run against a full standby compose stack in this session. |
| License scan | Not run: `nuget-license` is not installed in this environment. |

## Checklist

| ID | Result |
|---|---|
| U1 | PASS: money property tests. |
| U2 | PASS: transition and state-search tests. |
| U3 | PASS: ledger balance/property tests and the database append-only grant test. |
| U4 | PASS: same-key replay, different-hash 422 and in-flight/race integration tests. |
| C5 | PASS: 100 concurrent create requests with one key produced one payment, one PSP call and semantically identical answers. |
| C6 | PASS: all-approved 50-way run retried transient conflicts to exactly 10 successes and 40 rejections; refunded plus pending equals captured. A forced decline releases the reservation and allows the full amount to be refunded again. |
| C31a | PARTIAL: E1, E2, E3, E5, E6, E7, E8, E11, E12, E13, E16, E17, E18, E21, E25, E26, E27, E28 and E29 have API-level coverage. E4 and E9 are open escalations; no test was faked for either. |
| C43 | PASS: all eight runner scenarios directly and through PgBouncer 1.26.1, on PostgreSQL 18.6. |
| S1 | PASS: the solution builds with the banned-API analyzer; the analyzer probe test passes. |
| S3 | PASS: `EXPLAIN` confirms the merchant/created index. `S3_each_endpoint_within_query_count` counts Npgsql commands for create (≤40), read (≤10), capture (≤40) and refund (≤50). Review: 29 hand-written commands, 113 bound parameters, no `select *`, no string-built SQL and no EF/DbContext tracking. |
| S5 | PARTIAL: analyzers are errors and the request-path projects have the banned-symbol file. The review checklist was run on 2026-11-06: no `Task.Run`, `.Result`, `.Wait()` or `GetAwaiter().GetResult()` in `src/`; no `lock` spans an `await`; only bounded `Task.WhenAll` over the abandoned-commit list; no `DbContext`. The independent review is still required at the review gate. |
| S6 | PASS: `dotnet list package --vulnerable --include-transitive` found no vulnerable packages. `nuget-license` 4.0.18 (Apache-2.0) is pinned in `.config/dotnet-tools.json`; `dotnet tool restore` and `dotnet tool run nuget-license -i OpenPayments.slnx -t` exited 0. `gitleaks 8.30.1 dir .` and `gitleaks git .` both exited 0 with no leaks across the working tree and 79 commits. |

## Named gaps

- C31a: E4 and E9, both escalated. E4 has no retention mechanism; E9 has no callback receiver.
- S5: independent review at the review gate; the self-review recorded above is not a substitute.
- The API starts and serves the documented routes, but the service is not yet wired into the
  compose stack and the k6 Phase 1 ramp has not been run. Task 17 is therefore not started.

## Task 17 preparation attempt

On 2026-11-06 the local compose `primary` and `pgbouncer` started, but `scripts/migrate.sh` blocked on synchronous replication because neither standby was available. Standby startup then failed with `max_connections = 300` against the existing primary volume's 1100. The migration was cancelled by the shutdown. No benchmark run id or `result.md` was produced, so this is recorded as an invalid preparation attempt, not a ramp result. The compose services were stopped afterwards.
