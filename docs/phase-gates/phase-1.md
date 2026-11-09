# Phase 1 exit record

Status: **not achieved** · Last updated: 2026-11-09

The build and its tests pass, but the Phase 1 checklist is not complete. The remaining gaps
are the independent S5 review and a valid step 6 run, which closes Task 17 under
[D53](../decisions.md#d53). S3's rules ([D48](../decisions.md#d48)) and the
[D47](../decisions.md#d47) mix are done. C31a's scope was corrected by [D46](../decisions.md#d46): E4 and E9 now belong to
C31b in Phase 2, so C31a is complete without a fake callback route or expiry job.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 12 projects, 0 warnings. |
| `dotnet test OpenPayments.slnx` | PASS at `7f3d4ad`: 365 tests. Domain 70, analyzers 1, mock PSP 17, checks 24, integration 39, acceptance 57, console 123, console browser 34. Phase 1's own projects were 196 tests at the time of the first record. |
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
- Task 17: the laptop limit search has valid runs through step 5. Step 6 is invalid because k6 dropped arrivals while starting VUs; the fix is in `7dd42a7` and needs a rerun. Results are in [BASELINE](../../benchmarks/BASELINE.md).
- `C` and `N` are not Phase 1 evidence: [D53](../decisions.md#d53) moves them to the first Linux run, before Stage 4.
- C31a has no open design gap after D46; edge cases E4 and E9 are C31b tests, not Stage 1 evidence.

How the stack was prepared and which harness defects were fixed along the way is in [Stage 1 progress](../gates/stage-1-progress.md) and [Development challenges](../development-challenges.md).
