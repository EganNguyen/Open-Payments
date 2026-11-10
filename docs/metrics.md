# Metrics

The metric contract between the API, the load harness, the dashboard and the correctness run. Task 14 emits these names; Task 11 reads them; this file is their one home.

Status: Draft · Last updated: 2026-11-09

The stack is in [`deploy/compose/observability.yaml`](../deploy/compose/observability.yaml), with Prometheus at `http://127.0.0.1:9090`, Grafana at `http://127.0.0.1:3000` (`admin` / `admin` locally) and the [overview dashboard](../deploy/compose/grafana/dashboards/overview.json).

## Naming and cardinality

- `op_` means an Open Payments application metric. PostgreSQL and PgBouncer metrics keep their exporter names.
- Labels are bounded. Never add `merchant_id`, `payment_id`, `attempt_id` or a raw error message as a label.
- Counters end in `_total`; durations end in `_seconds`; ages end in `_seconds`; gauges state the current value.
- A rate is computed in PromQL, not emitted as a gauge.
- An inapplicable check or a missing stage is reported as not applicable, never as a zero-valued pass.

## Application metrics

| Metric | Type | Labels | Meaning | Emitted by |
|---|---|---|---|---|
| `op_requests_admitted_total` | counter | none | Requests admitted by the in-flight and rate limiters | API edge |
| `op_requests_rejected_total` | counter | `reason` | Requests refused before work. Reasons emitted today, mapped from the status code: `auth` (401, 403), `not_found` (404), `invalid_state` (409, 422), `rate_limit` (429), `busy` (503). `inflight_cap`, `pool_wait` and `lock_timeout` are planned but not separated yet: all three are counted as `busy` | API edge |
| `op_payments_completed_total` | counter | `outcome` | Payments that reached a final, committed outcome. Outcomes emitted today: `captured`, `authorized`, `declined` (excluding `fraud_blocked` and `timeout`). `action_required`, `canceled`, `voided` and `expired` are planned. The count starts at zero when the API process starts, so compare it with the database only for a run that began with the process | Payment flow, after the final commit only |
| `op_platform_overhead_seconds` | histogram | none | Total request time minus PSP wait. The SLO applies to this, not to end-to-end time | Payment flow |
| `op_threadpool_queue` | gauge | none | Pending ThreadPool work items | API `/metrics` |
| `op_gen2_gc_per_second` | gauge | none | Gen 2 collections per second since process start | API `/metrics` |
| `op_allocation_rate_bytes_per_second` | gauge | none | Managed allocation rate since process start | API `/metrics` |
| `op_abandoned_commits` | gauge | none | Commits still running after the commit wait limit ([E1](gates/stage-1-escalations.md#e1-task-13-npgsql-commit-cancellation-breaks-d28)) | Transaction runner, via API `/metrics` |
| `op_commit_warnings_total` | counter | none | Commit-time replication warnings that make the outcome unknown ([E1](gates/stage-1-escalations.md#e1-task-13-npgsql-commit-cancellation-breaks-d28)) | Transaction runner, via API `/metrics` |
| `op_unresolved_count`, `op_unresolved_oldest_age_seconds`, `op_unresolved_amount_minor` | gauge | `kind`, `state`, `currency`, `psp` | Payments in `PSP_PENDING` or `UNKNOWN`, and attempts and refunds in `PENDING` or `UNKNOWN`: how many, the oldest's age, and the money they hold. Their outcome isn't established, and Stage 1 can't resolve them ([runbook](runbooks/unknown-outcomes.md)) | postgres-exporter query `op_unresolved` |
| `op_psp_wait_seconds` | histogram | none | Time spent inside the PSP adapter call | PSP adapter |
| `op_inflight` | gauge | none | Requests currently in flight on this instance | API edge |
| `op_tx_retries_total` | counter | `sqlstate` | Transaction retries. Only `40001` and `40P01` are retried, at most twice | Transaction runner |
| `op_conditional_update_zero_rows_total` | counter | none | Conditional updates that changed no row, so the caller re-read and answered from current state | Payment store |
| `op_outbox_oldest_age_seconds` | gauge | none | Age of the oldest unpublished outbox row. **Not emitted yet** | Outbox publisher, Phase 3 |
| `op_payments_by_state` | gauge | `state` | Current payment count by state. **Not emitted yet** | Reconciler, Stage 2 |

`op_platform_overhead_seconds` buckets, in seconds: `0.001, 0.0025, 0.005, 0.01, 0.015, 0.02, 0.025, 0.03, 0.04, 0.05, 0.06, 0.075, 0.1, 0.2, 0.5, 1`. The cluster between 25 ms and 100 ms is deliberate, because the p99 target is 100 ms and the p50 target is 25 ms.

`op_psp_wait_seconds` buckets, in seconds: `0.01, 0.025, 0.05, 0.1, 0.2, 0.25, 0.5, 1, 2.5, 5, 10`. These cover the fixed 200 ms mock and the 2.5 s request budget.

A histogram with no samples is not a pass. The harness reports a missing metric as invalid, not as zero.

## Exporter metrics used by the dashboard

| Series | Source | Use |
|---|---|---|
| `pg_stat_database_deadlocks{datname="openpayments"}` | postgres-exporter | Deadlocks actually raised, independent of application counters |
| `pg_wal_bytes` | postgres-exporter | WAL rate for G3 and G6 |
| `pg_stat_database_xact_commit`, `pg_stat_database_xact_rollback` | postgres-exporter | Commit and rollback rate |
| `pg_locks_count{datname="openpayments"}` | postgres-exporter | Locks held, checked against `lock_timeout` |
| `pgbouncer_pools_client_waiting_connections` | pgbouncer-exporter | Clients waiting for a server connection |
| `pgbouncer_pools_client_maxwait_seconds` | pgbouncer-exporter | Age of the oldest waiting client |
| `process_cpu_seconds_total{job="api"}`, `process_resident_memory_bytes{job="api"}`, `process_num_threads{job="api"}` | API `/metrics` | API resources |
| `op_threadpool_queue`, `op_gen2_gc_per_second`, `op_allocation_rate_bytes_per_second` | API `/metrics` | Runtime pressure and allocation rate |
| `op_abandoned_commits`, `op_commit_warnings_total` | API `/metrics` | The two E1 commit signals |

The API scrape target is `host.docker.internal:8080` in local development. On Linux the compose file supplies `host.docker.internal:host-gateway`. In Kubernetes, the scrape config is replaced by service discovery.

## Results log coverage

The [Results log](scaling-playbook.md#results-log) asks for these fields. The dashboard or the harness supplies each.

| Results log field | Source |
|---|---|
| Admitted / rejected / completed payments per second | `rate(op_requests_admitted_total[1m])`, `sum by (reason) (rate(op_requests_rejected_total[1m]))`, `sum by (outcome) (rate(op_payments_completed_total[1m]))` |
| Platform overhead p50/p95/p99 | `histogram_quantile` over `op_platform_overhead_seconds_bucket` |
| End-to-end latency against a fixed PSP latency | k6 `http_req_duration` in `benchmarks/runs/<run-id>/k6-summary.json`; the app cannot see the client's full path |
| Error rate excluding deliberate rejections | Harness result, from `op_payments_completed_total` and the API's error responses |
| API CPU and memory | `process_cpu_seconds_total`, `process_resident_memory_bytes` |
| DB CPU, I/O and WAL rate | postgres-exporter (`pg_wal_bytes`, `pg_stat_database*`) and host metrics in the run record |
| Runtime ThreadPool queue, Gen 2 GC/s, allocation rate | `op_threadpool_queue`, `op_gen2_gc_per_second`, `op_allocation_rate_bytes_per_second` from `/metrics` |
| Pool wait, lock waits, deadlocks, WAL rate | `pgbouncer_pools_client_maxwait_seconds`, `pg_locks_count`, `pg_stat_database_deadlocks`, `rate(pg_wal_bytes[1m])` |
| Outbox age | `op_outbox_oldest_age_seconds` |
| Consumer lag | Kafka exporter, Phase 3 |
| Correctness CK1-CK10 | `op-checks` output, not a metric; the harness writes it to `checks.json` |

## Console dashboard

The console's home dashboard ([plan](superpowers/plans/2026-11-09-operations-dashboard.md)) reads Prometheus through `Console:Metrics:PrometheusUrl`; an empty or unreachable URL leaves its throughput panel unavailable, never zero. It uses:

- `sum(rate(op_payments_completed_total[5m]))`, `op_requests_admitted_total` and `op_requests_rejected_total` for throughput and rejections.
- `histogram_quantile` over `op_platform_overhead_seconds_bucket` and `op_psp_wait_seconds_bucket` for platform and PSP latency.
- `op_inflight`, `pg_stat_database_deadlocks{datname="openpayments"}`, `pgbouncer_pools_client_maxwait_seconds` and `op_outbox_oldest_age_seconds` for reliability.

The lifecycle and correctness cards read the replica directly, not Prometheus.

## Local stack

```bash
docker compose -f deploy/compose/compose.yaml up -d --wait primary pgbouncer
docker compose -f deploy/compose/observability.yaml up -d --wait
curl -sf http://127.0.0.1:9090/-/ready
```

The two files use the same Compose project name, so the exporters reach `primary:5432` and `pgbouncer:6432` by service name. Start the database stack first. If it is down, Prometheus still starts; the exporter panels show `pg_up 0` and no samples, which the harness must not read as success.

Check `http://127.0.0.1:9090/api/v1/targets` shows `postgres`, `pgbouncer` and `api` up before using a run's metrics. The dashboard is provisioned at **Open Payments / Open Payments Overview**.

## Alerts and gates

The metric stack is a measurement tool, not a gate. A missed gate, including a failed correctness check, makes the run fail even if every panel is green. Alerts and thresholds are in the [scaling playbook](scaling-playbook.md#measures-and-gates) and are not duplicated here.
