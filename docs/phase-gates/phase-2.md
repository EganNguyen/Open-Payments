# Phase 2 exit record

Status: **build complete on the mock; exit gate not passed** · Last updated: 2026-11-11

Stage 2's design was signed on 2026-11-10 and built from `57e63c2` to `8751927`. Tasks 19-31,
console C3/C5/C12 with N3/N4, and Task 35 are built and recorded in
[Stage 2 progress](../gates/stage-2-progress.md). Milestone I (Tasks 32-34) is blocked because the
owner has not supplied the Stripe sandbox keys in `deploy/compose/secrets/`; Task 36's benchmark
hours have not run; and Task 37's consolidated `C31b` test was not written. No row in
[verification](../verification.md) is ticked by this record.

## Evidence

| Check | Result |
|---|---|
| `dotnet build OpenPayments.slnx -warnaserror` | PASS: 0 warnings, 0 errors at `8751927`. |
| `dotnet test OpenPayments.slnx` | PASS at `8751927` on 2026-11-11: **607 tests, 0 failed**. Domain 95, analyzers 1, mock PSP 45, checks 25, reconciler 35, console 184, integration 66, console browser 47, acceptance 109. |
| `dotnet list OpenPayments.slnx package --vulnerable --include-transitive` | PASS: no vulnerable packages from the configured source. |
| License scan | PASS: `dotnet tool restore` and `dotnet tool run nuget-license -i OpenPayments.slnx -t` exited 0. |
| Secret scan | PASS on the tracked tree and history: `git archive HEAD` into a temporary directory then `gitleaks dir` found no leaks, and `gitleaks git .` scanned 246 commits with no leaks. A filesystem-wide scan reports two generic-key false positives inside Playwright's bundled `bin/` JavaScript, not in tracked files. |
| `scripts/check-migration.sh` | PASS: all migrations, 32 hot-table partitions, fixture and grant checks, including `0005_settlement.sql`. |
| `scripts/check-db-settings.sh` | **FAIL** on the local stack: `quorum standbys: expected 2, got 0`. The standbys were not streaming after the laptop restarted; PgBouncer and the direct timeouts passed. This is an environment failure, not a code finding. |
| Prometheus rules | PASS: 12 rules pass `promtool check rules`. |

## Phase 2 IDs

| ID | Result |
|---|---|
| C3 | PASS: authorize, capture and refund timeouts stay unresolved; the reconciler recovers each mode; one PSP effect (Task 27). |
| C4 | PARTIAL: the lease and conditional-update recovery path is tested; the crash harness that kills the API after a PSP approval is not built. |
| C9 | PARTIAL: duplicate, reorder and redelivery are covered by callback tests; the property permutation test is not written. |
| C12 | PASS: PSP 429 is retried once under the same key; no duplicate charge (Task 23). |
| C13 | PASS on the mock: breaker opens on PSP outage and fails fast; two-account failover is mock-only. |
| C15 | PASS: transaction-B failures leave the payment pending or 202 and the reconciler completes it; L7/L8 cover lost answers. |
| C16 | PARTIAL: DNS-failure alert and typed failure pass; the TLS certificate probe and expiry metric have an alert but no probe until Task 32. |
| C23 | NOT RUN: the 5,000 TPS / 2 s latency C23 run needs the benchmark hours and Linux; the laptop cannot answer it. |
| C26 | PASS: two reconcilers and a lapsed lease claim once (Task 27). |
| C31b | PARTIAL: E4 and E24 pass in `C31bKeyExpiryTests`/`ExpiryTests`; E9, E10 and E19 have callback tests; E22 and E23 have settlement tests; E20 (3-D Secure) is blocked with Task 33. The single consolidated `C31bEdgeCaseTests` class is not written. |
| C32 | PARTIAL: callback and API paths are covered; lower-case and unknown-currency fixtures wait for the Stripe adapter. |
| C34 | PARTIAL: the deadlock/retry path is covered by domain and store tests; the forced three-way race with the reconciler is not written. |
| C36, C37 | PASS on reruns: one capture/authorize wins, the loser gets status or 409; the reconciler does not change that. |
| C38 | PARTIAL: late approval moves to `REVIEW` and no ledger; automatic late void is not built. |
| C39 | BLOCKED: no Stripe sandbox keys. |
| C40 | PASS on the mock: duplicate, unmatched, wrong-amount and missing lines end in defined states; duplicates and a killed ingest post once; discrepancies have an alert. |
| C41 | PASS: an UNKNOWN payment past 24 h keeps its key, retry returns state with no second charge; resolved keys expire. |
| C44, C45 | PASS on the mock: each recovery mode is exercised; past the replay window no replay is sent. |
| C46 | PARTIAL: parked retry and early-event application pass; the kill-between-receipt-and-commit harness is not written. |
| C47 | PASS: recovery spends its own lane and leaves live traffic alone; backlog drains. |
| C50 | PARTIAL: capture/void race, live-capture expiry wait and timer-vs-manual capture pass; void timeout and auth-hold CK7 rest on the domain matrix. |
| C51 | PASS: credential rejection fails fast with a stable code, opens only the account breaker and pages. |
| U5 | PARTIAL: capabilities and the mock contract pass; recorded Stripe fixtures wait for Task 32. |
| L7, L8 | PASS: a PSP answer is never dropped; post-authorization failures answer 202 and the capture completes once. |
| E3 checks | PASS: `resolved_at` is written by every resolving path; expiry is the D64 method. |
| G2 | BLOCKED: no sandbox accounts or written live limits; `docs/benchmarks/g2-per-account.md` is not written. |

## Console and tooling

| Item | Result |
|---|---|
| Console C3 | PASS: escalation, manual resolution, refund resolution and late-approval reversal in `Decide`, with the attempt-state table. |
| Console C5 | PASS: `ResolutionStore` applies an approved proposal in one transaction; the internal route needs `console:resolve`, is gated by `Internal:Enabled`, returns 403 on self-approval and 409 when the payment moved. |
| Console C12 | PASS: propose and decide pages, second-proposal redirect, evidence encoded as text, approve through the internal route. |
| N3, N4 | PASS: full queue pages, `?from=` case panel, htmx-capable refresh, many-unresolved banner, 390 px first-row check. |
| Task 35 | PASS: PSP events and last inquiry on the timeline, with the payment index used. |

## Named gaps

- Stripe adapter, 3-D Secure flow and contract freeze (Tasks 32-34), pending `stripe-a.env`,
  `stripe-b.env` and the O5 decision.
- Stage 2 benchmark: steps 0-5 with the phase2 mock profile, the 500 req/s × 10 min runs and the
  250 req/s × 1 h run (Task 36 step 3/3b). `benchmarks/run.sh` has no phase2 profile yet.
- G2 in writing, which only the PSP can give.
- The consolidated C31b test class and the missing C4, C9, C34, C46 harnesses named above.
- `scripts/check-db-settings.sh` on this laptop, until the standbys stream again.
- `op_escalations_total` and the reconciler-side metrics are counted and logged but have no
  scrape endpoint; the alerts use the postgres exporter instead.

## Handover

The build is ready for an independent review from `57e63c2` to `8751927`, with the gaps above
stated rather than hidden. Milestone I must not be reviewed as done until the sandbox keys arrive.
