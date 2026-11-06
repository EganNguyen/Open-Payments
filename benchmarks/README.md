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
| `resources.json` | Resource fields and their sources for the run's Prometheus window |
| `shares.json` | Achieved request shares from the k6 summary |

`run.sh` exits non-zero if k6 fails its threshold, `op-checks` fails, or `result.md` says the run is invalid or a gate is missed.

## The mix

`k6/mix.js` drives the [D47](../docs/decisions.md#d47) mix in one open-model `ramping-arrival-rate` scenario, so the offered rate is fixed even when the system slows. The operation is chosen by a weighted roll: one-call sale, authorize, standalone capture, status, refund, retry and other. Callbacks are the mock's job, not k6's.

With `TWO_CALL_SHARE=0.2` (the default), half of all requests create a payment. Of those, 20% are two-call: 10% of all requests authorize and 10% capture. The remaining 40% are one-call sales. The full shares are 40% sale, 10% authorize, 10% capture, 15% status, 10% refund, 10% retry and 5% other.

- New payments use unique keys. The retry share reuses a key from a per-VU ring buffer.
- Refund targets come from `REFUND_TARGETS`, a JSON array seeded from `seed-refundable.sql`, so generators need no shared state. If no seed exists, the per-VU ring is the fallback.
- Merchants are picked 80/20, using the local `k6/lib/keys.js` helper. No import from a URL runs at load time.
- `TWO_CALL_SHARE` defaults to 0.2. It is recorded in `config.json`. A capture consumes an authorized payment from the per-VU queue; an empty queue sends an authorize instead and increments `k6_capture_substitutions_total`.
- Before the load, `setup()` creates up to 200 authorized payments to seed those queues. They are real completed authorizations and are included in the completed count.
- `shares.json` records the achieved request counts and shares. A share more than 2 percentage points from its D47 target makes the run invalid.
- 429 and 503 are counted as `k6_requests_rejected_total`, separately from `op_unexpected_error`. Declines (402) and conflicts (409) are expected outcomes, not errors.

## Mock and database

| Variable | Default | Use |
|---|---|---|
| `MOCK_URL` | `http://127.0.0.1:18080` | Mock PSP admin and route base |
| `API_URL` | `http://127.0.0.1:8080` | API base when `TARGET_OVERRIDE=api` |
| `TARGET_OVERRIDE` | `api` | `mock` sends the mix to the mock routes instead |
| `API_TOKEN` | empty | Bearer token for the API |
| `TWO_CALL_SHARE` | `0.2` | Share of payments that authorize then capture separately |
| `PREFILL_PER_VU` | `3` | Authorized payments created in `setup()` per preallocated VU, capped at 200 |
| `CHECKS_DB_URL` | `postgres://postgres:op_local_only@127.0.0.1:15432/openpayments` | Database `op-checks` reads |
| `EFFECTS_PATH` | `<run-dir>/effects.csv` | Effect log for CK2 |
| `MOCK_EFFECTS_DIR` | unset | Copy the mock's live `effects.csv` into the run directory |
| `REFUND_SEED_COUNT` | `1000` | Rows selected by `seed-refundable.sql` |
| `SNAPSHOT_CMD` | unset | Command that resets to the prefilled snapshot |
| `DRAIN_SECONDS` | `3` | Wait after k6 before `op-checks` |
| `OP_CHECKS_BIN` | `tools/OpenPayments.Checks/bin/Debug/net10.0/OpenPayments.Checks.dll` | Check runner |

## Full benchmark reset

A full reset empties the payment and ledger tables and the CK1 manifest. Stop the API and
the mock first, because a request that writes during the reset would leave the manifest and
the ledger disagreeing. Clear `ck.ledger_rows` only in the same transaction that truncates
the ledger, never on its own, during a run, or to make a check pass.

```bash
docker compose -f deploy/compose/compose.yaml stop api mock-psp
psql postgres://postgres:op_local_only@127.0.0.1:15432/openpayments -v ON_ERROR_STOP=1 <<'SQL'
BEGIN;
TRUNCATE TABLE payments, payment_attempts, refunds, ledger_transactions, ledger_entries,
  outbox, idempotency_keys, psp_events, settlement_lines, benchmark_refundable CASCADE;
TRUNCATE TABLE ck.ledger_rows;
COMMIT;
SQL
psql postgres://postgres:op_local_only@127.0.0.1:15432/openpayments -v ON_ERROR_STOP=1 \
  -f benchmarks/seed-local.sql
```

Then reset the effect log with the mock still stopped, as described below, and start both
services again. Before starting a run, confirm `ck.ledger_rows` and `ledger_entries` are both
empty.

## Resetting the effect log

Stop the mock before emptying `effects.csv`. `EffectLog` opens the file with
`FileMode.Append` and keeps its write offset, so truncating the file while the mock is
running leaves zero bytes between the header and the next append. `op-checks` then fails
to parse the log and no checks run.

Use this sequence:

```bash
docker compose -f deploy/compose/compose.yaml stop mock-psp
printf 'key,op,amount_minor,currency,at\n' > deploy/compose/mock-effects/effects.csv
docker compose -f deploy/compose/compose.yaml start mock-psp
```

Verify the file has the header and no NUL bytes before starting a run:

```bash
python3 - <<'PY'
from pathlib import Path
data = Path("deploy/compose/mock-effects/effects.csv").read_bytes()
assert data.startswith(b"key,op,amount_minor,currency,at\n"), data[:40]
assert b"\x00" not in data, "effect log contains NUL padding"
print("effect log clean:", len(data), "bytes")
PY
```

Never truncate the live file, and stop the API with the mock when resetting the database so
no request can append between the truncate and the restart.

`MOCK_ADDED_P99_MS` is recorded but not measured by this script. The mock's own capacity run is in [mock-psp-capacity.md](mock-psp-capacity.md); set this value from a fresh measurement before trusting a Linux run. A value above 5 makes the run invalid.

`resources.py` fills the result's resource fields from each run's own Prometheus window. API CPU, memory, ThreadPool queue, Gen 2 GC and allocation come from the API `/metrics` process and runtime series. Pool wait is `pgbouncer_pools_client_maxwait_seconds`; deadlocks are `pg_stat_database_deadlocks`; WAL rate is `rate(pg_wal_bytes[1m])`. Lock waits use `pg_locks_count` as a lock-held proxy. Database CPU and I/O come from `docker stats` for `DB_CONTAINER` (default `open-payments-primary-1`). Outbox age and consumer lag stay `n/a` until their phases.

## Tests

```bash
.venv/bin/python -m pytest benchmarks/test_report.py -v
```

The tests are the validity rules themselves: dropped arrivals, generator CPU, mock latency, a failed applicable check, an inapplicable check shown as n/a, a metric/database mismatch, the achieved mix shares, and the preview label.

## Limitations

- A laptop run is a **preview**. Only a Linux run from Phase 3 onward, with all applicable checks and a clean tree, is a recorded result.
- The harness reads `http_req_duration` and subtracts the configured PSP latency to show platform overhead. The authoritative platform-overhead histogram is `op_platform_overhead_seconds` in Prometheus; the dashboard is in `deploy/compose/grafana/dashboards/overview.json`.
- Consumer lag is not measured before Phase 3, and `op-checks` reports CK6 as not applicable without `--prom`.
