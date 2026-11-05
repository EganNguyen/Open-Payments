# Stage 1 Build Progress

The build gate's evidence log: one line per task, with the commit, the command that was run, and the result. Task 7 is complete; tasks 8-17 are not started.

Status: In progress · Last updated: 2026-11-05

| Task | Commit | Test command | Result |
|---|---|---|---|
| 7 | `840578b` | `dotnet test --filter "U1\|E11\|S1"`; `dotnet test`; `dotnet build -warnaserror`; `dotnet list OpenPayments.slnx package --vulnerable --include-transitive`; `nuget-license -i OpenPayments.slnx -t` | PASS: 12 tests (11 Money, 1 banned-API), 0 warnings, 0 vulnerable packages, licenses allowed. Probe file confirmed `RS0030` fails a `Domain` build. |
