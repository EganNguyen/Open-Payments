# Phase 1 exit record

Status: **not achieved** · Last updated: 2026-11-06

The build and its tests pass, but the Phase 1 checklist is not complete. The gaps are listed
by name below; the gate stays open until they are closed.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 12 projects, 0 warnings. |
| `dotnet test OpenPayments.slnx` | PASS: 183 tests. Domain 70, analyzers 1, mock PSP 17, checks 17, integration 31, acceptance 47. |
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
| C6 | PARTIAL: 50 concurrent refunds never exceeded captured, but the test does not force 50 successful reservations and does not prove every declined reservation released. |
| C31a | PARTIAL: E1, E2, E3, E6, E7, E8, E11, E12, E13, E17, E21, E25, E26, E27, E28 and E29 have API-level coverage. E4, E5, E9, E16 and E18 are not yet covered by an acceptance test; the domain suite covers parts of their rules. |
| C43 | PASS: all eight runner scenarios directly and through PgBouncer 1.26.1, on PostgreSQL 18.6. |
| S1 | PASS: the solution builds with the banned-API analyzer; the analyzer probe test passes. |
| S3 | PARTIAL: `EXPLAIN` confirms the merchant/created index for the list query. Query-count tests per endpoint and the generated-SQL/tracking review are not written. |
| S5 | PASS with review: analyzers are errors and the request-path projects have the banned-symbol file. The review still needs a second person. |
| S6 | PARTIAL: vulnerability scan passed; the license scan and secret scan were not run. |

## Named gaps

- C31a: E4, E5, E9, E16, E18.
- C6: prove the full 50-way reservation outcome and release path.
- S3: endpoint query-count tests and the generated-SQL/tracking review.
- S6: license and secret scanning.
- S5: independent review.
- The API starts and serves the documented routes, but the service is not yet wired into the
  compose stack and the k6 Phase 1 ramp has not been run. Task 17 is therefore not started.
