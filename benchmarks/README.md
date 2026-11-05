# Load Harness

Drives one [ramp step](../docs/scaling-playbook.md#ramp-plan) with the [example mix](../docs/scaling-playbook.md#workload-and-load-generation), then records the run and its correctness checks.

**A run is not proof if it is invalid.** The harness refuses a dirty git tree, records the configuration, and `report.py` marks the run invalid if the generator dropped arrivals, used more than 80% CPU, or the mock added more than 5 ms at p99. A fast run with a failed check is a failed run.

## Quick start

```bash
# One mock and one database with 0001_initial.sql applied
EFFECTS_DIR=/tmp/op-effects dotnet tools/OpenPayments.MockPsp/bin/Debug/net10.0/OpenPayments.MockPsp.dll &

# The API arriving in Task 14 listens on :8080; the mock is on :18080.
benchmarks/run.sh 0 --env local --stage phase1 --psp-latency 200
```

The Phase 0 exit runs the harness against the mock directly:

```bash
TARGET_OVERRIDE=mock benchmarks/run.sh 0 --env local --stage phase0
```

Expected: a run directory with `checks.json` showing failures for no data, and a non-zero exit. That proves the checks run automatically and cannot pass on empty data.

## What a run writes

`benchmarks/runs/<run-id>/` (git-ignored):

| File | Contents |
|---|---|
| `config.json` | Commit, step, rate, duration, mock configuration, generator CPU, environment, validity inputs |
| `k6-summary.json` | k6's summary export: rates, percentiles, dropped iterations |
| `k6.log`, `k6.time` | k6 output and `/usr/bin/time -p` accounting |
| `checks.json` | `op-checks` output, including `applicable` and `passed` for CK1-CK10 |
| `checks.stdout`, `checks.stderr` | The check runner's output and error |
| `counts.json` | Completed-payment metric and database count, when the stage applies |
| `result.md` | The [Results log](../docs/scaling-playbook.md#results-log) filled in, with the preview/recorded label and gate result |

`run.sh` exits non-zero if k6 fails its threshold, `op-checks` fails, or `result.md` says the run is invalid or a gate is missed.

## The mix

`k6/mix.js` drives the shares in one open-model `ramping-arrival-rate` scenario, so the offered rate is fixed even when the system slows. The operation is chosen by a weighted roll: 60% payment, 15% status, 10% refund, 10% retry, 5% other. Callbacks are the mock's job, not k6's.

- New payments use unique keys. The retry share reuses a key from a per-VU ring buffer.
- Refund targets come from `REFUND_TARGETS`, a JSON array seeded from `seed-refundable.sql`, so generators need no shared state. If no seed exists, the per-VU ring is the fallback.
- Merchants are picked 80/20, using the local `k6/lib/keys.js` helper. No import from a URL runs at load time.
- `AUTO_CAPTURE_SHARE` defaults to 0.5. It is recorded in `config.json`, because the auto-capture share changes the completed-payment rate.
- 429 and 503 are counted as `k6_requests_rejected_total`, separately from `op_unexpected_error`. Declines (402) and conflicts (409) are expected outcomes, not errors.

## Mock and database

| Variable | Default | Use |
|---|---|---|
| `MOCK_URL` | `http://127.0.0.1:18080` | Mock PSP admin and route base |
| `API_URL` | `http://127.0.0.1:8080` | API base when `TARGET_OVERRIDE=api` |
| `TARGET_OVERRIDE` | `api` | `mock` sends the mix to the mock routes instead |
| `API_TOKEN` | empty | Bearer token for the API |
| `CHECKS_DB_URL` | `postgres://postgres:op_local_only@127.0.0.1:15432/openpayments` | Database `op-checks` reads |
| `EFFECTS_PATH` | `<run-dir>/effects.csv` | Effect log for CK2 |
| `MOCK_EFFECTS_DIR` | unset | Copy the mock's live `effects.csv` into the run directory |
| `REFUND_SEED_COUNT` | `1000` | Rows selected by `seed-refundable.sql` |
| `SNAPSHOT_CMD` | unset | Command that resets to the prefilled snapshot |
| `DRAIN_SECONDS` | `3` | Wait after k6 before `op-checks` |
| `OP_CHECKS_BIN` | `tools/OpenPayments.Checks/bin/Debug/net10.0/OpenPayments.Checks.dll` | Check runner |

`MOCK_ADDED_P99_MS` is recorded but not measured by this script. The mock's own capacity run is in [mock-psp-capacity.md](mock-psp-capacity.md); set this value from a fresh measurement before trusting a Linux run. A value above 5 makes the run invalid.

## Tests

```bash
.venv/bin/python -m pytest benchmarks/test_report.py -v
```

The tests are the validity rules themselves: dropped arrivals, generator CPU, mock latency, a failed applicable check, an inapplicable check shown as n/a, a metric/database mismatch, and the preview label.

## Limitations

- A laptop run is a **preview**. Only a Linux run from Phase 3 onward, with all applicable checks and a clean tree, is a recorded result.
- The harness reads `http_req_duration` and subtracts the configured PSP latency to show platform overhead. The authoritative platform-overhead histogram is `op_platform_overhead_seconds` in Prometheus once the API emits it; the dashboard is in `deploy/compose/grafana/dashboards/overview.json`.
- Consumer lag is not measured before Phase 3, and `op-checks` reports CK6 as not applicable without `--prom`.
