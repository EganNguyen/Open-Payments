# Stage 2 Build Progress

The build gate's evidence log: one line per task, with the commit, the command that was run, and the result. Plan: [Stage 2 plan](../superpowers/plans/2026-11-10-stage-2-psp-and-recovery.md). Design: [Stage 2 design gate](stage-2-design.md), signed 2026-11-10; build starts from `57e63c2`. Escalations live in [Stage 2 escalations](stage-2-escalations.md).

Status: **In build** · Last updated: 2026-11-10

| Task | Commit | Test command | Result |
|---|---|---|---|
| 19 | `9b2d4d6` | `dotnet test tests/OpenPayments.Integration.Tests --filter Stage2SchemaTests`; `scripts/check-migration.sh`; `dotnet build OpenPayments.slnx -warnaserror`; `dotnet test OpenPayments.slnx` | PASS, test first: 8 of 8 schema tests failed before `0004` (`relkind` `r`, missing columns, no autovacuum options). After: 8/8 pass. `check-migration.sh` passes (32 partitions, no spike tables, fixtures). Whole suite 467 tests, 0 failed; build 0 warnings. `psp_events` recreated hash-partitioned 16 ways; `payment_attempts` and `refunds` gain key/recovery columns; `psp_accounts` gains `callback_secret_ref` and `recovery_share`; each key and event partition gets the expiry autovacuum settings. `ConsoleSchemaTests`' grant list updated for the approved `DELETE` on `psp_events`. |
