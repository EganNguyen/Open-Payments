"""Collect the resource fields for one run from its own Prometheus time window."""

import argparse
import json
import subprocess
import urllib.parse
import urllib.request


def query_range(prom, expression, start, end):
    params = urllib.parse.urlencode({
        "query": expression,
        "start": str(start),
        "end": str(end),
        "step": "15",
    })
    try:
        with urllib.request.urlopen(prom + "/api/v1/query_range?" + params, timeout=10) as response:
            payload = json.load(response)
    except Exception:
        return []
    result = payload.get("data", {}).get("result", [])
    values = []
    for series in result:
        for _, value in series.get("values", []):
            try:
                values.append(float(value))
            except (TypeError, ValueError):
                continue
    return values


def average(values):
    return round(sum(values) / len(values), 2) if values else None


def maximum(values):
    return round(max(values), 2) if values else None


def docker_stat(container):
    try:
        output = subprocess.check_output(
            ["docker", "stats", "--no-stream", "--format",
             "{{.CPUPerc}}|{{.BlockIO}}", container],
            text=True, stderr=subprocess.DEVNULL, timeout=10).strip()
    except Exception:
        return None, None
    if not output or "|" not in output:
        return None, None
    cpu, block_io = output.split("|", 1)
    return cpu.rstrip("%"), block_io


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--prom", required=True)
    parser.add_argument("--start", required=True, type=int)
    parser.add_argument("--end", required=True, type=int)
    parser.add_argument("--db-container", default="open-payments-primary-1")
    parser.add_argument("--out", required=True)
    args = parser.parse_args()

    api_cpu = average(query_range(
        args.prom, "rate(process_cpu_seconds_total{job=\"api\"}[1m]) * 100", args.start, args.end))
    api_memory = maximum(query_range(
        args.prom, "process_resident_memory_bytes{job=\"api\"}", args.start, args.end))
    threadpool_queue = maximum(query_range(
        args.prom, "op_threadpool_queue", args.start, args.end))
    gen2 = average(query_range(
        args.prom, "op_gen2_gc_per_second", args.start, args.end))
    allocation = average(query_range(
        args.prom, "op_allocation_rate_bytes_per_second", args.start, args.end))
    pool_wait = maximum(query_range(
        args.prom, "pgbouncer_pools_client_maxwait_seconds", args.start, args.end))
    lock_waits = maximum(query_range(
        args.prom, "pg_locks_count{datname=\"openpayments\"}", args.start, args.end))
    deadlocks = maximum(query_range(
        args.prom, "pg_stat_database_deadlocks{datname=\"openpayments\"}", args.start, args.end))
    wal_rate = average(query_range(
        args.prom, "rate(pg_wal_bytes[1m])", args.start, args.end))
    db_cpu, db_io = docker_stat(args.db_container)

    resources = {
        "window_start": args.start,
        "window_end": args.end,
        "api_cpu_pct": api_cpu,
        "api_memory": f"{int(api_memory)} bytes" if api_memory is not None else None,
        "db_cpu_pct": db_cpu,
        "db_io": db_io,
        "threadpool_queue": threadpool_queue,
        "gen2_gc_per_s": gen2,
        "allocation_rate": f"{int(allocation)} bytes/s" if allocation is not None else None,
        "pool_wait": f"{pool_wait} s" if pool_wait is not None else None,
        "lock_waits": f"{lock_waits} locks held (pg_locks_count; proxy for lock waits)",
        "deadlocks": deadlocks,
        "wal_rate": f"{int(wal_rate)} bytes/s" if wal_rate is not None else None,
        "sources": {
            "api": "process_cpu_seconds_total, process_resident_memory_bytes, op_threadpool_queue, op_gen2_gc_per_second, op_allocation_rate_bytes_per_second",
            "db_cpu_io": "docker stats " + args.db_container,
            "pool_wait": "pgbouncer_pools_client_maxwait_seconds",
            "lock_waits": "pg_locks_count (locks held, not a wait-event count)",
            "deadlocks": "pg_stat_database_deadlocks",
            "wal_rate": "rate(pg_wal_bytes[1m]) from postgres-exporter",
        },
    }
    with open(args.out, "w", encoding="utf-8") as handle:
        json.dump(resources, handle, indent=2)
        handle.write("\n")


if __name__ == "__main__":
    main()
