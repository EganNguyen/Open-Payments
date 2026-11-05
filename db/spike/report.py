"""Judges spike runs. Pure functions, so the rules that decide G1 are tested."""
import math
import re

CPU_LIMIT_PCT = 60
GENERATOR_LIMIT_PCT = 80
KEEPS_UP_RATIO = 0.98

# Database commits in one run of each workload.
COMMITS = {
    "sale": 2,
    "auth-capture": 4,
    "decline": 2,
    "refund": 2,
    "callback": 1,
    "outbox-mark": 1,
    "outbox-marker": 1,
    "key-expiry": 1,
}


def required_commits(payments, commits_per_payment, refunds, callback_commits):
    return payments * commits_per_payment + refunds * 2 + callback_commits


def commits_per_script(weights):
    total = sum(weights.values())
    return sum(COMMITS[name] * weight for name, weight in weights.items()) / total


def sustained_rate(rows, field="achieved_tps"):
    """The highest rate that was valid, correct, kept up, and within the CPU gate."""
    rates = [
        r[field]
        for r in rows
        if r["valid"]
        and r["verify_passed"]
        and r["primary_cpu_pct"] is not None
        and r["primary_cpu_pct"] <= CPU_LIMIT_PCT
        and r["achieved_tps"] >= r["target_tps"] * KEEPS_UP_RATIO
    ]
    return max(rates, default=0)


def is_valid(generator_cpu_pct, failed, aborted):
    return generator_cpu_pct <= GENERATOR_LIMIT_PCT and failed == 0 and not aborted


def percentile(values, p):
    if not values:
        return None
    ordered = sorted(values)
    return ordered[max(0, math.ceil(len(ordered) * p / 100) - 1)]


def latencies_ms(log_lines):
    """Open-model latency from a pgbench log: time running plus time waiting to start."""
    result = []
    for line in log_lines:
        fields = line.split()
        running_us = int(fields[2])
        lag_us = int(fields[6]) if len(fields) > 6 else 0
        result.append((running_us + lag_us) / 1000)
    return result


def parse_summary(text):
    tps = re.search(r"^tps = ([\d.]+)", text, re.M)
    failed = re.search(r"number of failed transactions: (\d+)", text)
    return {
        "tps": float(tps.group(1)) if tps else 0.0,
        "failed": int(failed.group(1)) if failed else 0,
        "aborted": "aborted" in text,
    }


def aborted_clients(text):
    """Clients pgbench stopped because a statement failed. In production each is an error response."""
    return len(re.findall(r"aborted in command", text))


def commit_latency_ms(text):
    """Mean of the `commit` lines in pgbench's per-statement report, if there are any."""
    values = [float(m) for m in re.findall(r"^\s+([\d.]+)\s+\d+\s+commit;?\s*$", text, re.M)]
    return round(sum(values) / len(values), 3) if values else None


def generator_cpu_seconds(text):
    """Children's CPU from the shell builtin `times`, which is the last two lines' second line."""
    matches = re.findall(r"^(\d+)m([\d.]+)s (\d+)m([\d.]+)s$", text, re.M)
    if len(matches) < 2:
        return None
    um, us, sm, ss = matches[-1]
    return int(um) * 60 + float(us) + int(sm) * 60 + float(ss)


def parse_stats(text):
    """CPU cores per Compose service from `docker stats`. 100% is one core."""
    cores = {}
    for line in text.splitlines():
        name, percent = line.split()
        match = re.fullmatch(r"open-payments-(.+)-\d+", name)
        cores[match.group(1) if match else name] = float(percent.rstrip("%")) / 100
    return cores


def parse_ps(text):
    """CPU cores per service from `ps -Ao %cpu=,command=` on the native stack.

    Backends carry the cluster name in their title, and each PgBouncer its config file.
    """
    cores = {}
    for line in text.splitlines():
        parts = line.split(None, 1)
        if len(parts) < 2:
            continue
        match = re.match(r"postgres: (\w+):", parts[1]) or re.search(r"pgbouncer -d \S*/(pgbouncer\d+)\.ini", parts[1])
        if match:
            cores[match.group(1)] = cores.get(match.group(1), 0.0) + float(parts[0]) / 100
    return {name: round(value, 3) for name, value in cores.items()}


def parse_iostat(text):
    """Transfers/s and MB/s from the last sample of `iostat -d -c 2 -w 1 disk0`."""
    samples = re.findall(r"^\s*[\d.]+\s+([\d.]+)\s+([\d.]+)\s*$", text, re.M)
    if not samples:
        return None
    tps, mb_s = samples[-1]
    return {"tps": float(tps), "mb_s": float(mb_s)}


def parse_memory_free(text):
    match = re.search(r"memory free percentage: (\d+)%", text)
    return int(match.group(1)) if match else None


def top_waits(counts, n):
    """The commonest wait events as shares of all samples."""
    total = sum(counts.values())
    if not total:
        return None
    ordered = sorted(counts.items(), key=lambda item: -item[1])[:n]
    return ", ".join(f"{name} {round(100 * count / total)}%" for name, count in ordered)


def _cell(value):
    return "n/a" if value is None else value


def compare(runs):
    """One Markdown table across runs, as {run name: rows}, for reading variants side by side."""
    lines = [
        "| Run | Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Aborted clients | Kept up | Verified | Top waits |",
        "|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for name, rows in runs.items():
        for r in rows:
            kept_up = r["achieved_tps"] >= r["target_tps"] * KEEPS_UP_RATIO
            cells = [
                name, r["variant"], r["target_tps"], r["achieved_tps"], r.get("commits_per_s"),
                r.get("latency_p50_ms"), r.get("latency_p99_ms"), r.get("aborted_clients"),
                "yes" if kept_up else "no", "yes" if r["verify_passed"] else "no", r.get("waits"),
            ]
            lines.append("| " + " | ".join(str(_cell(c)) for c in cells) + " |")
    return "\n".join(lines) + "\n"


def render_summary(rows, environment, required):
    lines = ["# Spike Summary", ""]
    if environment != "linux":
        lines += ["**Local: comparative only, does not answer G1.**", ""]
    lines += [
        f"Environment: {environment}. Required commits/s for the target mix: {required}.",
        f"Gates: primary CPU at most {CPU_LIMIT_PCT}%, the rate kept up with, verification passed.",
        "",
        "| Variant | Target/s | Achieved/s | Commits/s | p50 ms | p99 ms | Commit ms | Primary CPU % | Primary cores | Busiest PgBouncer cores | Generator CPU % | WAL MB/s | Deadlocks | Verify | Result | Top waits |",
        "|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|---|",
    ]
    for r in rows:
        status = "invalid" if not r["valid"] else "fails verify" if not r["verify_passed"] else "ok"
        cells = [
            r["variant"], r["target_tps"], r["achieved_tps"], r.get("commits_per_s"),
            r.get("latency_p50_ms"), r.get("latency_p99_ms"), r.get("commit_ms"),
            r["primary_cpu_pct"], r.get("primary_cores"), r.get("pgbouncer_core_max"), r.get("generator_cpu_pct"),
            r.get("wal_mb_s"), r.get("deadlocks"), r["verify_passed"], status, r.get("waits"),
        ]
        lines.append("| " + " | ".join(str(_cell(c)) for c in cells) + " |")
    lines += ["", "## Sustained commits/s by variant", ""]
    for name in dict.fromkeys(r["variant"] for r in rows):
        subset = [r for r in rows if r["variant"] == name and "commits_per_s" in r]
        lines.append(f"- {name}: {sustained_rate(subset, 'commits_per_s') if subset else 'n/a'}")
    return "\n".join(lines) + "\n"
