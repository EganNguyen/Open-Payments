# Stage 1 Build Progress

The build gate's evidence log: one line per task, with the commit, the command that was run, and the result. Task 7 is complete; tasks 8-17 are not started.

Status: In progress · Last updated: 2026-11-05

| Task | Commit | Test command | Result |
|---|---|---|---|
| 7 | `840578b` | `dotnet test --filter "U1\|E11\|S1"`; `dotnet test`; `dotnet build -warnaserror`; `dotnet list OpenPayments.slnx package --vulnerable --include-transitive`; `nuget-license -i OpenPayments.slnx -t` | PASS: 12 tests (11 Money, 1 banned-API), 0 warnings, 0 vulnerable packages, licenses allowed. Probe file confirmed `RS0030` fails a `Domain` build. |
| 8 | `1dd7279` plus `dac68e8`, `c7c8a21`, `99970c0` | `dotnet test tests/OpenPayments.MockPsp.Tests`; `dotnet test`; `dotnet build -warnaserror`; k6 capacity runs | PASS: 17 mock tests; 29 total tests, 0 warnings. Capacity: valid 10k req/s preview at 1.34 ms added p99; 25k unproven on the laptop, all invalid runs recorded in `benchmarks/mock-psp-capacity.md`. |
| 9 | `f421e07` | `dotnet test tests/OpenPayments.Checks.Tests`; `dotnet test`; `dotnet build -warnaserror`; `scripts/check-migration.sh`; `op-checks --stage phase0` and `phase3` on an empty database | PASS: 17 check tests (CK1-CK10 corruption, zero rows, applicability, clean fixture), 46 tests total, 0 warnings; migration check passed on PostgreSQL 18.6; CLI wrote the result JSON and exited 1 with zero data. |
| 10 | `552a218` | `docker compose -f deploy/compose/compose.yaml up -d --wait primary pgbouncer`; `docker compose -f deploy/compose/observability.yaml up -d --wait`; `curl -sf localhost:9090/-/ready`; `docker compose config --quiet`; `dotnet test` | PASS: Prometheus ready, Grafana healthy, postgres and pgbouncer scrape targets up; API target down only until Task 14; 46 tests, 0 warnings. |
