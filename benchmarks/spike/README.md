# Database Spike

How to run the spike that answers gates G1, G3 and G4, and how to read what it writes. The questions it answers are in the [spec](../../docs/superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#what-the-spike-must-answer).

Status: Runner proven on the MacBook, 2026-11-05. **No result here answers G1 yet.** The [benchmark guide](../../docs/benchmarks/benchmark-guide.md) has the full steps, and the [MacBook summary](../../docs/benchmarks/spike/macbook-2026-11-05/summary.md) reads the results.

## Run it

Local, to check the machinery. Numbers are comparative only:

```bash
docker compose -f deploy/compose/compose.yaml up -d --wait
```

```bash
MEASURE_SECONDS=30 WARMUP_SECONDS=5 db/spike/run-matrix.sh
```

On Linux, for the real answer, with a primary and two standbys on NVMe, WAL on its own volume, and pgbench on a separate machine:

```bash
SPIKE_ENV=linux SPIKE_CPU_COMMAND='<prints the primary CPU percent>' DB_ADMIN_URL=... DB_URL=... DB_BOUNCER_ADMIN_URL=... db/spike/run-matrix.sh
```

`ONLY=baseline,V13` runs just those variants. Settings are in [`db/spike/matrix.json`](../../db/spike/matrix.json).

## Read it

Each run writes `benchmarks/spike/<run-id>/`: `summary.md`, `result.json`, and a git-ignored `raw/` with pgbench's output and latency samples.

- **Compare commits/s against the required rate, not achieved/s.** Mixes differ in commits per script, so a script rate isn't comparable between them.
- **Only rows that pass count:** valid, verified, kept up with the rate, and primary CPU at 60% or less, with a real measurement.
- **Latency is open-model:** it includes time a transaction waited to start, so a slow system can't hide.

## Known limits

- **One read outside each transaction.** pgbench can't make UUIDs, so each script fetches its IDs with one read a real client wouldn't need. This slightly overstates the load.
- **No PSP wait.** It holds no connection in the real flow, so it isn't modeled.
- **Local CPU comes from `docker stats` or `ps`,** which are coarse samples every few seconds.
- **The progress marker option is a load measurement only.** It can skip a transaction that commits late, so choosing it needs a commit-safe position.
- **One PgBouncer process caps throughput at about 0.99 core.** The MacBook setup uses four. Linux needs the same check.
- **Results depend on prefill size.** A variant that changes schema or merchant count reloads data, which takes hours at 100M, so plan the Linux run around it.

## Runs

| Run | Environment | Purpose | Reading |
|---|---|---|---|
| [20261105-131705-local](20261105-131705-local/summary.md) | Laptop, 100 scripts/s, 30 s per row | Prove the runner | All 20 variants ran, no failures, verification passed. **Not a capacity result.** Two p99 spikes to watch on Linux: V6 (287 ms) and V8-m10000 (173 ms). Too little data here to explain either |
| [20261105-134749-macbook](20261105-134749-macbook/summary.md) | MacBook, baseline only, 5M prefill, on battery, 60 s per rate | Find the limit | Kept up to about 2,000 sales/s (4,000 commits/s), with p99 under 100 ms only at 1,000/s or below. **Preview, not a G1 answer.** While saturated at 3,000/s, a short sample showed the pool full (100 servers active, 147 clients waiting) with most backends in `ClientRead`, then `WALWrite` and `SyncRep`. This early reading was incomplete: [later runs](../../docs/benchmarks/spike/macbook-2026-11-05/summary.md#bottlenecks) showed port forwarding and one PgBouncer were the first limits, and that the VM's flush isn't durable |

The `limit-*` runs, from 2026-11-05 14:52 on, find what limits throughput. The [summary](../../docs/benchmarks/spike/macbook-2026-11-05/summary.md) explains them. The `native-a-hash-pool100-unsafe-fsync` run is mislabeled, since it left the standbys flushing, so don't use it.

An earlier macbook attempt failed during loading, with a non-round payment count and leftover rows. The cause wasn't found. The runner now stops if the count is wrong, and the rerun was clean.
