# Phase 1 exit record

Status: **checklist passed; D56 laptop verification not yet run** · Last updated: 2026-11-10

Every Phase 1 checklist row passes. The two gaps this record named on 2026-11-09 are closed: the
[review gate](../gates/stage-1-review.md) closed on 2026-11-10, which is S5's independent review, and
Task 17 closed at step 6's failed p99 gate under the amended [D53](../decisions.md#d53). What remains is
the laptop-scaled verification the owner fixed in [D56](../decisions.md#d56); its rows are
[below](#d56-laptop-verification), and none has run yet. S3's rules ([D48](../decisions.md#d48)) and the
[D47](../decisions.md#d47) mix are done. C31a's scope was corrected by [D46](../decisions.md#d46): E4 and E9 now belong to
C31b in Phase 2, so C31a is complete without a fake callback route or expiry job.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 12 projects, 0 warnings. |
| `dotnet test OpenPayments.slnx` | PASS at `13e851a` on 2026-11-10: 457 tests, 0 failed. Domain 70, analyzers 1, mock PSP 23, checks 24, console 166, integration 45, console browser 46, acceptance 82. Phase 1's own projects were 196 tests at the time of the first record. |
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
| S5 | PASS: analyzers are errors and the request-path projects have the banned-symbol file. The self-review on 2026-11-06 found no `Task.Run`, `.Result`, `.Wait()` or `GetAwaiter().GetResult()` in `src/`, no `lock` across an `await`, and no `DbContext`. The independent review is the [Stage 1 review gate](../gates/stage-1-review.md), closed 2026-11-10 with no critical, high or medium finding open. |
| S6 | PASS: `dotnet list package --vulnerable --include-transitive` found no vulnerable packages. `nuget-license` 4.0.18 (Apache-2.0) is pinned in `.config/dotnet-tools.json`; `dotnet tool restore` and `dotnet tool run nuget-license -i OpenPayments.slnx -t` exited 0. `gitleaks 8.30.1 dir .` and `gitleaks git .` both exited 0 with no leaks across the working tree and git history. |

## D56 laptop verification

The pass conditions are fixed in [D56](../decisions.md#d56) before any run. Every result is a preview:
none replaces L3, L5, C1, C2 or C7 on Linux, and C10 and C11 aren't run. The *stable rate* is the
highest ramp step with valid runs whose gates all pass. Run IDs and numbers go in
[BASELINE](../../benchmarks/BASELINE.md); this table points to them.

| Check | Run | Pass condition | Result |
|---|---|---|---|
| Capacity | Step 4 once and step 6 three times, each from a reset, two API instances behind nginx (`deploy/compose/two-api.yaml`) | Valid runs whose gates all pass; sets the stable rate | Not run |
| Soak | 60 min at the stable rate | Gates hold; CK1-CK6 pass; memory, Gen 2 GC, ThreadPool queue and pool wait show no upward trend | Not run, needs the stable rate |
| L3, scaled | 3× the stable rate for 10 min | Excess rejected with 429 or 503; no restart or OOM; platform p99 under 100 ms within 2 min of the burst ending; CK1-CK6 pass | Not run, needs the stable rate |
| C1, scaled | Primary CPU throttled to about 5× slower, at the stable load | Fast 503 shedding; `40P01`/`40001` retries under 1% of requests; no ThreadPool starvation; CK1-CK6 pass after | Not run, needs the stable rate |
| C2 | PgBouncer pool shrunk below demand under load, then restored | Waits bounded by `query_wait_timeout` and failed as 503; recovery with no restart | Not run |
| C7, partial | Primary killed under load; the standby with the higher replay position promoted by hand | RPO 0: every payment answered 200 or 202 is on the new primary; CK1 and CK3 pass. Failover time isn't judged | Not run |
| Alerts | `CommitWarning`, `AbandonedCommitsStuck` and `UnresolvedOperationAging` in `deploy/compose/prometheus/alerts.yml` | Each seen firing in a running Prometheus | Not run |

## Named gaps

- The D56 rows above.
- `C` and `N` are not Phase 1 evidence: [D53](../decisions.md#d53) moves them, the mock PSP's 25,000 req/s check and step 6's scaling on real hardware to the first Linux run, before Stage 4.
- C31a has no open design gap after D46; edge cases E4 and E9 are C31b tests, not Stage 1 evidence.

How the stack was prepared and which harness defects were fixed along the way is in [Stage 1 progress](../gates/stage-1-progress.md) and [Development challenges](../development-challenges.md).
