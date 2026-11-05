"""Runs the spike matrix. Settings come from matrix.json and the environment.

Each variant changes one thing from the baseline. Each rate gets a warm-up, then a measured
run at a fixed arrival rate, and the data is verified afterward.
"""
import glob
import json
import os
import pathlib
import resource
import statistics
import subprocess
import tempfile
import threading
import time

import report

HERE = pathlib.Path(__file__).parent
ROOT = HERE.parent.parent
COMPOSE = ["docker", "compose", "-f", str(ROOT / "deploy/compose/compose.yaml")]

ADMIN = os.environ.get("DB_ADMIN_URL", "postgres://postgres:op_local_only@127.0.0.1:15432/openpayments")
APP = os.environ.get("DB_URL", "postgres://op_app:op_local_only@127.0.0.1:16432/openpayments")
BOUNCER_ADMIN = os.environ.get("DB_BOUNCER_ADMIN_URL", "postgres://postgres:op_local_only@127.0.0.1:16432/pgbouncer")
ENVIRONMENT = os.environ.get("SPIKE_ENV", "local")
MEASURE = int(os.environ.get("MEASURE_SECONDS", "600"))
WARMUP = int(os.environ.get("WARMUP_SECONDS", "120"))
ONLY = [v for v in os.environ.get("ONLY", "").split(",") if v]
RATES = [int(r) for r in os.environ.get("RATES", "").split(",") if r]  # overrides the matrix's rates
CPU_COMMAND = os.environ.get("SPIKE_CPU_COMMAND")  # on Linux: prints the primary's CPU percent


def admin_env():
    return {**os.environ, "PGOPTIONS": "-c synchronous_commit=local"}


def sql(command):
    return subprocess.run(["psql", ADMIN, "-XAtq", "-v", "ON_ERROR_STOP=1", "-c", command],
                          check=True, capture_output=True, text=True, env=admin_env()).stdout.strip()


def sql_file(path, *variables):
    args = ["psql", ADMIN, "-Xq", "-v", "ON_ERROR_STOP=1"]
    for v in variables:
        args += ["-v", v]
    result = subprocess.run(args + ["-f", str(HERE / path)], capture_output=True, text=True, env=admin_env())
    if result.returncode != 0:
        raise RuntimeError(f"{path} failed: {result.stderr.strip()[-800:]}")


def reset(settings):
    print(f"  loading schema {settings['schema']}, prefill {settings['prefill']}", flush=True)
    sql_file(f"schema-{settings['schema']}.sql")
    sql_file("seed.sql", f"merchants={settings['merchants']}")
    remaining = settings["prefill"]
    while remaining > 0:
        batch = min(remaining, 1_000_000)
        sql_file("seed-batch.sql", f"merchants={settings['merchants']}", f"batch={batch}")
        remaining -= batch
        expected = settings["prefill"] - remaining
        actual = int(sql("select count(*) from payments"))
        print(f"    prefilled {actual} of {settings['prefill']}", flush=True)
        if actual != expected:
            raise RuntimeError(f"expected {expected} payments, found {actual}: something else is writing to this database")
    sql_file("seed-finish.sql", f"refundable={settings['refundable']}")


def set_pool(size):
    subprocess.run(["psql", BOUNCER_ADMIN, "-XAtq", "-c", f"set default_pool_size = {size}"],
                   check=True, capture_output=True, text=True)


def container_cpu(service):
    """A container's CPU as a percent of the whole machine it can use."""
    name = subprocess.run(COMPOSE + ["ps", "-q", service], capture_output=True, text=True).stdout.strip()
    out = subprocess.run(["docker", "stats", "--no-stream", "--format", "{{.CPUPerc}}", name],
                         capture_output=True, text=True).stdout.strip().rstrip("%")
    cpus = int(subprocess.run(["docker", "info", "--format", "{{.NCPU}}"], capture_output=True, text=True).stdout)
    return float(out) / cpus if out else 0.0


class Sampler(threading.Thread):
    def __init__(self):
        super().__init__(daemon=True)
        self.stop_flag = threading.Event()
        self.primary, self.bouncer = [], []

    def run(self):
        while not self.stop_flag.wait(5):
            if CPU_COMMAND:
                self.primary.append(float(subprocess.run(CPU_COMMAND, shell=True, capture_output=True, text=True).stdout))
            else:
                self.primary.append(container_cpu("primary"))
                self.bouncer.append(container_cpu("pgbouncer"))


class SlotReader(threading.Thread):
    """V9's log-capture option: reads committed changes from a replication slot."""

    def __init__(self):
        super().__init__(daemon=True)
        self.stop_flag = threading.Event()

    def run(self):
        while not self.stop_flag.wait(1):
            sql("select count(*) from pg_logical_slot_get_changes('spike_capture', null, null)")


def pgbench(settings, rate, seconds, threads, log_prefix=None):
    args = ["pgbench", APP, "-n", "-M", "extended", "-R", str(rate), "-T", str(seconds),
            "-c", str(settings["clients"]), "-j", str(threads)]
    for name in ("merchants", "entries", "rls", "pipeline", "refundable", "batch"):
        args += ["-D", f"{name}={settings[name]}"]
    for name, weight in settings["scripts"].items():
        args += ["-f", f"{HERE}/tx-{name}.pgbench@{weight}"]
    if log_prefix:
        args += ["-l", "--log-prefix", log_prefix, "--sampling-rate", "0.1"]
        if not settings["pipeline"]:
            args += ["-r"]
    return subprocess.run(args, capture_output=True, text=True)


def measure(variant, settings, rate, raw_dir):
    threads = settings["threads"]
    if WARMUP:
        pgbench(settings, rate, WARMUP, threads)
    before = sql("select pg_current_wal_lsn(), deadlocks from pg_stat_database where datname = current_database()").split("|")
    used_before = resource.getrusage(resource.RUSAGE_CHILDREN)
    sampler = Sampler()
    sampler.start()
    prefix = str(raw_dir / f"{variant}-{rate}")
    result = pgbench(settings, rate, MEASURE, threads, prefix)
    sampler.stop_flag.set()
    used_after = resource.getrusage(resource.RUSAGE_CHILDREN)
    after = sql("select pg_current_wal_lsn(), deadlocks from pg_stat_database where datname = current_database()").split("|")
    wal_bytes = int(sql(f"select pg_wal_lsn_diff('{after[0]}', '{before[0]}')"))

    lines = []
    for path in glob.glob(prefix + ".*"):
        with open(path) as handle:
            lines += handle.readlines()
    latencies = report.latencies_ms(lines)

    summary = report.parse_summary(result.stdout + result.stderr)
    cpu_seconds = (used_after.ru_utime + used_after.ru_stime) - (used_before.ru_utime + used_before.ru_stime)
    generator_cpu = round(100 * cpu_seconds / (MEASURE * threads), 1)

    verify = subprocess.run(["psql", ADMIN, "-XAtq", "-v", "ON_ERROR_STOP=1", "-f", str(HERE / "verify.sql")],
                            capture_output=True, text=True)
    (raw_dir / f"{variant}-{rate}.out").write_text(result.stdout + result.stderr + verify.stderr)
    return {
        "variant": variant,
        "target_tps": rate,
        "achieved_tps": round(summary["tps"], 1),
        "commits_per_s": round(summary["tps"] * report.commits_per_script(settings["scripts"]), 1),
        "latency_p50_ms": report.percentile(latencies, 50),
        "latency_p99_ms": report.percentile(latencies, 99),
        "commit_ms": report.commit_latency_ms(result.stdout),
        "primary_cpu_pct": round(statistics.mean(sampler.primary), 1) if sampler.primary else None,
        "pgbouncer_cpu_pct": round(statistics.mean(sampler.bouncer), 1) if sampler.bouncer else None,
        "generator_cpu_pct": generator_cpu,
        "wal_mb_s": round(wal_bytes / MEASURE / 1_000_000, 2),
        "deadlocks": int(after[1]) - int(before[1]),
        "failed": summary["failed"],
        "verify_passed": verify.returncode == 0,
        "valid": report.is_valid(generator_cpu, summary["failed"], summary["aborted"]),
    }


def main():
    matrix = json.loads((HERE / "matrix.json").read_text())
    run_id = time.strftime("%Y%m%d-%H%M%S") + "-" + ENVIRONMENT
    out = ROOT / "benchmarks/spike" / run_id
    raw = out / "raw"
    raw.mkdir(parents=True)
    rows, loaded = [], None
    tokens = report.required_commits(**matrix["required"])
    power = subprocess.run(["pmset", "-g", "batt"], capture_output=True, text=True).stdout
    (out / "environment.txt").write_text(f"environment: {ENVIRONMENT}\nmeasure: {MEASURE}s, warm-up: {WARMUP}s\n{power}")

    for variant in matrix["variants"]:
        if ONLY and variant["name"] not in ONLY:
            continue
        settings = {**matrix["baseline"], **matrix["environments"][ENVIRONMENT], **variant.get("set", {})}
        print(f"{variant['name']}: {variant['why']}", flush=True)
        identity = (settings["schema"], settings["merchants"], settings["prefill"])
        if identity != loaded:
            reset(settings)
            loaded = identity
        for path in variant.get("setup", []):
            sql_file(path)
        for command in variant.get("setup_system", []):
            sql(command)
        if variant.get("setup_system"):
            sql("select pg_reload_conf()")
        set_pool(settings["pool"])
        reader = None
        if settings.get("log_capture"):
            sql("select pg_create_logical_replication_slot('spike_capture', 'test_decoding')")
            reader = SlotReader()
            reader.start()
        try:
            for rate in RATES or matrix["environments"][ENVIRONMENT]["rates"]:
                row = measure(variant["name"], settings, rate, raw)
                rows.append(row)
                print(f"  rate {rate}: {row['achieved_tps']}/s p99 {row['latency_p99_ms']} ms valid={row['valid']} verify={row['verify_passed']}", flush=True)
                (out / "result.json").write_text(json.dumps(rows, indent=2))
        finally:
            if reader:
                reader.stop_flag.set()
                reader.join(5)
                sql("select pg_drop_replication_slot('spike_capture')")
            for command in variant.get("teardown_system", []):
                sql(command)
            if variant.get("teardown_system"):
                sql("select pg_reload_conf()")
            for path in variant.get("teardown", []):
                sql_file(path)
            set_pool(matrix["baseline"]["pool"])

    (out / "summary.md").write_text(report.render_summary(rows, ENVIRONMENT, tokens))
    print(f"Wrote {out}", flush=True)


if __name__ == "__main__":
    main()
