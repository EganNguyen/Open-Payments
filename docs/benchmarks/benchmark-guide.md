# Benchmark Guide

How to deploy the stack, run benchmarks, watch resources, verify the money and compare results on the MacBook. The design is in [Local deployment](../architecture/local-deployment.md), and the latest results are in the [MacBook summary](spike/macbook-2026-11-05/summary.md). What's still unmeasured is in [Open experiments](open-experiments.md).

Status: Database tier only. The full platform has no benchmark until it's built · Last updated: 2026-11-06

Run every command from the repository root.

## What a run can prove

| Result | Meaning |
|---|---|
| Native stack, verified, kept up, no aborts | A durable result for this Mac, on the database path |
| Native at overload | The saturation ceiling. Its latency is only queueing, and it grows with run length |
| Docker stack | How the software behaves with fast flushes. **Not durable, so never a result** |
| Anything on this Mac | **Doesn't answer G1 or prove production capacity** |

## 1. Prerequisites

Install the tools. This adds PostgreSQL 18, PgBouncer and the test runner's environment:

```bash
brew install postgresql@18 pgbouncer libpq
```

```bash
python3 -m venv .venv && .venv/bin/pip install pytest
```

Put `psql` and `pgbench` on your path, for this shell:

```bash
export PATH="/opt/homebrew/opt/libpq/bin:$PATH"
```

Check the runner's rules still pass:

```bash
.venv/bin/python -m pytest db/spike/test_report.py -q
```

Before measuring, plug in the Mac, keep the lid open, close heavy apps and stop the other stack. This shows the power source and memory free:

```bash
pmset -g batt; memory_pressure -Q | tail -1
```

Only for the Docker stack: set Docker Desktop to 18 CPUs and 40 GB in Settings, Resources.

## 2. Deploy and check

Start the native stack. The first run creates the data. It exits non-zero unless all seven services are healthy, flushes are real, and both standbys stream in quorum:

```bash
scripts/native-stack.sh up
```

Check it again at any time:

```bash
scripts/native-stack.sh status
```

Check the database settings the design requires, through both the primary and PgBouncer:

```bash
DB_ADMIN_URL=postgres://postgres:op_local_only@127.0.0.1:25432/openpayments DB_DIRECT_URL=postgres://op_app:op_local_only@127.0.0.1:25432/openpayments DB_URL=postgres://op_app:op_local_only@127.0.0.1:26432/openpayments scripts/check-db-settings.sh
```

To use the Docker stack instead, stop the native one first. This builds and starts it, and waits for health checks:

```bash
scripts/native-stack.sh down
```

```bash
PG_CONFIG=primary.macbook.conf STANDBY_SHARED_BUFFERS=6GB docker compose -f deploy/compose/compose.yaml --profile spike up -d --wait
```

## 3. Configure a workload

Settings are in [`db/spike/matrix.json`](../../db/spike/matrix.json). The `macbook` environment uses schema C, pool 200, 800 clients and 5M prefilled payments.

| Variable | Does | Example |
|---|---|---|
| `ONLY` | Runs these variants | `ONLY=baseline,V13` |
| `RATES` | Target **scripts/s**, one measurement per rate | `RATES=1000,1500` |
| `MEASURE_SECONDS`, `WARMUP_SECONDS` | Length of each measurement and warm-up. Defaults: 600 and 120 | `MEASURE_SECONDS=60` |
| `REUSE_DATA` | Keeps loaded data for the first variant. Loading 5M takes several minutes | `REUSE_DATA=1` |
| `SPIKE_LABEL` | Added to the run's folder name | `SPIKE_LABEL=trial` |
| `SPIKE_SET` | Overrides settings as JSON | `SPIKE_SET='{"pool":400}'` |

**Target rates count scripts, not payments.** Commits per script differ by variant, so compare commits/s. In V13, 90 of every 211 scripts are sales, so 1,000 payments/s means `RATES=2344`.

## 4. Run one variant

This loads the data, then runs the baseline for 60 s per rate. It takes about 10 minutes:

```bash
ONLY=baseline RATES=1000,1500,2000 MEASURE_SECONDS=60 WARMUP_SECONDS=10 db/spike/run-native.sh
```

This runs the full mix on the data already loaded:

```bash
REUSE_DATA=1 ONLY=V13 RATES=1172,2344 MEASURE_SECONDS=60 WARMUP_SECONDS=10 db/spike/run-native.sh
```

Other variants are listed in the matrix, such as `V1` for quorum off or `V5` for time partitions. A variant that changes schema, merchants or prefill reloads the data automatically.

**Treat `V1` and `V2` as cost measurements only.** They turn off a design guarantee, so they can't be adopted from a benchmark.

## 5. Run the suite

This runs all 20 variants at the `macbook` rates. It takes about 2 hours with 60 s per rate:

```bash
MEASURE_SECONDS=60 WARMUP_SECONDS=10 db/spike/run-native.sh
```

**The whole matrix hasn't been run on the native stack yet.** Only `baseline` and a short `V13` have, so expect to fix variant setup scripts on schema C.

Find what limits throughput. This offers far more load than the stack can take and changes one suspect each step, so compare only the achieved rate. It uses the Docker stack:

```bash
db/spike/find-limit.sh layers
```

Compare pool sizes on the native stack with a rate search:

```bash
RATE=1000,1500,2000,2500 SCHEMA=c-hash-all POOLS="200 400" MEASURE_SECONDS=60 WARMUP_SECONDS=10 db/spike/find-limit.sh native
```

Compare one cell against two with the same total memory and data. Both cells share one SSD, so this can't show what cells do with their own disks. **It has only had a short smoke test, so it has no results yet:**

```bash
db/spike/two-cells.sh one_cell
```

```bash
db/spike/two-cells.sh two_cells
```

`db/spike/find-limit.sh diagnostics` turns off real flushes and quorum to split up their costs. **Its results are never durable results.** Check `scripts/native-stack.sh status` afterward.

**Don't edit a script while it's running.** Bash reads scripts as it goes, so an edit can make it re-run steps. This happened once here.

## 6. Collect metrics

Each row in `result.json` already records these per measurement:

| Area | Fields |
|---|---|
| Throughput and latency | `achieved_tps`, `commits_per_s`, `latency_p50_ms`, `latency_p99_ms`, `commit_ms` |
| Errors | `failed`, `aborted_clients`, `deadlocks` |
| CPU, in cores | `primary_cores`, `standby_cores`, `pgbouncer_core_max`, `all_cores`, `generator_cpu_pct` |
| Memory | `memory_free_pct_min` |
| Disk | `disk_tps`, `disk_mb_s`, `wal_mb_s` |
| Database | `waits`, `pool_active`, `pool_waiting` |
| Correctness | `verify_passed`, `valid` |

To watch live, use a second terminal.

Top processes by CPU:

```bash
top -l 0 -s 5 -n 12 -o cpu -stats pid,command,cpu,mem
```

Disk transfers and MB/s:

```bash
iostat -d -w 5 disk0
```

Memory:

```bash
vm_stat 5
```

Loopback network bytes. Only replication and client traffic use it here:

```bash
netstat -ib -I lo0
```

What backends are waiting on right now:

```bash
PGPASSWORD=op_local_only psql -h 127.0.0.1 -p 25432 -U postgres -d openpayments -c "select coalesce(wait_event, 'running') as wait, count(*) from pg_stat_activity where backend_type = 'client backend' and state <> 'idle' group by 1 order by 2 desc"
```

WAL totals and flush time:

```bash
PGPASSWORD=op_local_only psql -h 127.0.0.1 -p 25432 -U postgres -d openpayments -c "select wal_records, wal_fpi, pg_size_pretty(wal_bytes), wal_buffers_full from pg_stat_wal" -c "select sum(fsyncs) as flushes, round(sum(fsync_time)::numeric / nullif(sum(fsyncs), 0), 2) as mean_ms from pg_stat_io where object = 'wal'"
```

Replication state and lag:

```bash
PGPASSWORD=op_local_only psql -h 127.0.0.1 -p 25432 -U postgres -d openpayments -c "select application_name, sync_state, state, write_lag, flush_lag, replay_lag from pg_stat_replication"
```

One PgBouncer's pool. Repeat for ports 26433-26435:

```bash
PGPASSWORD=op_local_only psql -h 127.0.0.1 -p 26432 -U postgres -d pgbouncer -c "show pools"
```

**Application metrics don't exist yet,** because the application isn't built. Task 10 adds observability, and Task 11 adds the load harness.

## 7. Spot saturation

| Signal | Means | Field or tool |
|---|---|---|
| `achieved_tps` below 98% of target | Fell behind | `result.json` |
| p50 jumps by 10× or more | A queue is growing | `latency_p50_ms` |
| `WALWrite` and `SyncRep` over about 80%, CPU low | Flush path limit | `waits`, `primary_cores` |
| `BufferContent` high | Writers fight over the same pages | `waits` |
| `ClientRead` high, pool full, PgBouncer near 1 core | Pooler limit | `waits`, `pgbouncer_core_max` |
| `aborted_clients` above 0 | Timeouts: production would return errors | `aborted_clients`, primary's log |
| Generator CPU over 80% | **The run is invalid**, as pgbench was the limit | `generator_cpu_pct`, `valid` |
| Memory free falling toward 10% | Memory pressure | `memory_free_pct_min` |

The primary's log names what timed out:

```bash
grep -aE 'canceling statement|still waiting for' ~/.local/share/open-payments-native/log/primary.log | tail -20
```

## 8. Verify correctness

The runner runs [`db/spike/verify.sql`](../../db/spike/verify.sql) after every rate, and writes `verify_passed` in the result. It fails on:

- an unbalanced ledger transaction
- refunded plus pending above captured
- `refund_pending_minor` that differs from unresolved refunds
- a captured payment with no capture transaction
- no payments at all

Run it by hand after any run. It prints nothing and exits 0 when the data is correct:

```bash
PGPASSWORD=op_local_only psql -h 127.0.0.1 -p 25432 -U postgres -d openpayments -XAtq -v ON_ERROR_STOP=1 -f db/spike/verify.sql; echo "exit $?"
```

**A row with `verify_passed` false is never a result, however fast.** `verify.sql` covers the spike's SQL only. The full checks, CK1-CK10, come with the application.

## 9. Compare results

Each run writes to `benchmarks/spike/<run-id>/`: `result.json`, `summary.md` and `environment.txt`, plus a git-ignored `raw/`. Runs stopped early have no `summary.md`.

This prints one table across runs:

```bash
python3 db/spike/compare.py benchmarks/spike/20261105-155517-macbook-limit-native-c-hash-all-pool200 benchmarks/spike/20261105-152946-macbook-limit-native-a-hash-pool200
```

Compare fairly:

- **Compare commits/s** between variants, since scripts differ.
- **Same stack and same prefill.** Runs grow the data, so a later run has more of it.
- **Repeat before believing a difference.** Here, p99 at 1,500/s varied from 128 to 209 ms between runs of the same setup. Treat a smaller gap as noise.
- **Ignore overload latency.** Compare it only for runs that kept up.

## 10. Clean up and reset

Stop the native stack and keep its data:

```bash
scripts/native-stack.sh down
```

Delete the native stack's data. Only this stack's folder is removed:

```bash
scripts/native-stack.sh reset
```

Delete a second cell's data, if you ran one:

```bash
CELL=2 scripts/native-stack.sh reset
```

Stop the Docker stack and keep its data:

```bash
docker compose -f deploy/compose/compose.yaml --profile spike stop
```

**This permanently deletes the Docker stack's databases:**

```bash
docker compose -f deploy/compose/compose.yaml --profile spike down -v
```

**Between runs:** leaving out `REUSE_DATA` reloads the data, which is the cleanest comparison. After overload runs, leave a minute for WAL flushing to settle.

## The application schema

The spike's scripts use their own schemas and **delete everything in the `public` schema when loading.** The application's schema is `db/migrations/`, so don't share a database between them.

Prove the migrations and money rules on a throwaway database. It's created and deleted for you:

```bash
DB_ADMIN_URL=postgres://postgres:op_local_only@127.0.0.1:25432/openpayments scripts/check-migration.sh
```

Apply migrations to an empty database. Each applies once, and an edited one is refused:

```bash
DB_ADMIN_URL=postgres://postgres:op_local_only@127.0.0.1:25432/openpayments scripts/migrate.sh
```

## The full platform

The API, fraud, PSP adapters, broker and workers don't exist yet, so this guide covers only the database tier. After Tasks 7-17, the platform benchmark runs through the [mock PSP and k6 harness](../superpowers/plans/2026-11-05-open-payments-implementation-and-benchmark.md) and replaces the calculated platform numbers in the [summary](spike/macbook-2026-11-05/summary.md#realistic-end-to-end-performance-expectations).
