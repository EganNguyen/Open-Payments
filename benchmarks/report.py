"""Turn one harness run into validity, gate results and result.md.

The rules are in the spec: a dropped arrival, a generator over 80% CPU, a mock that
adds more than 5 ms at p99, or a failed applicable check makes a run invalid or failed.
"""

import argparse
import json
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Any, Dict, List, Optional


@dataclass
class Validity:
    valid: bool
    reasons: List[str] = field(default_factory=list)
    not_checked: List[str] = field(default_factory=list)


@dataclass
class Gates:
    passed: bool
    reasons: List[str] = field(default_factory=list)


@dataclass
class CountCheck:
    ok: bool
    difference: int


def ck(name: str, passed: Optional[bool]) -> Dict[str, Any]:
    """Build one check entry. None means the check is not applicable."""
    return {
        "check": name,
        "applicable": passed is not None,
        "passed": bool(passed) if passed is not None else False,
    }


def render(check: Dict[str, Any]) -> str:
    """Render one check as pass, FAIL or n/a. n/a is never shown as passed."""
    name = check.get("check", "?")
    if not check.get("applicable", False):
        return "%s n/a (not applicable)" % name
    if check.get("passed", False):
        return "%s pass" % name
    return "%s FAIL" % name


def validity(
    summary: Dict[str, Any],
    gen_cpu: float,
    mock_added_p99_ms: Optional[float] = None,
    dirty_tree: bool = False,
    config_recorded: bool = True,
    completed_metric: Optional[int] = None,
    db_count: Optional[int] = None,
) -> Validity:
    reasons: List[str] = []
    not_checked: List[str] = []
    dropped = int(metric(summary, "dropped_iterations", "count", 0))
    if dropped > 0:
        reasons.append("generator dropped %d arrivals" % dropped)
    if gen_cpu > 80:
        reasons.append("generator CPU was %.1f%%, over 80%%" % gen_cpu)
    if mock_added_p99_ms is None:
        not_checked.append("mock added p99 latency")
    elif mock_added_p99_ms > 5:
        reasons.append(
            "mock added %.2f ms at p99, over 5 ms" % mock_added_p99_ms
        )
    if dirty_tree:
        reasons.append("git tree was dirty")
    if not config_recorded:
        reasons.append("configuration was not recorded")
    if completed_metric is None or db_count is None:
        not_checked.append("completed metric vs database count")
    else:
        counts = reconcile_counts(completed_metric, db_count)
        if not counts.ok:
            reasons.append(
                "completed metric %d differs from database %d"
                % (completed_metric, db_count)
            )
    return Validity(not reasons and not not_checked, reasons, not_checked)


def required_checks(stage: str) -> set:
    if stage == "phase0":
        return {"CK1", "CK2", "CK3"}
    return {"CK1", "CK2", "CK3", "CK4", "CK5", "CK6", "CK9", "CK10"}


def gates(
    p99: float,
    errors: float,
    checks: List[Dict[str, Any]],
    p50: Optional[float] = None,
    error_limit: float = 0.1,
    p99_limit: float = 100.0,
    p50_limit: float = 25.0,
) -> Gates:
    reasons: List[str] = []
    if not checks:
        reasons.append("no checks ran")
    if p99 > p99_limit:
        reasons.append("p99 %.2f ms is over %.0f ms" % (p99, p99_limit))
    if p50 is not None and p50 > p50_limit:
        reasons.append("p50 %.2f ms is over %.0f ms" % (p50, p50_limit))
    if errors > error_limit:
        reasons.append("error rate %.3f%% is over %.1f%%" % (errors, error_limit))
    for check in checks:
        if check.get("applicable", False) and not check.get("passed", False):
            reasons.append("%s failed" % check.get("check", "?"))
    return Gates(not reasons, reasons)


def reconcile_counts(metric: int, db: int) -> CountCheck:
    return CountCheck(metric == db, int(metric) - int(db))


def render_header(stage: str, env: str = "local") -> str:
    if env == "local" or stage != "phase3":
        label = "preview"
        why = "a laptop run or a stage before Phase 3, so it is comparative only"
    else:
        label = "recorded"
        why = "Linux, Phase 3 or later; still subject to the gates below"
    return "# Run result: %s\n\n_%s._\n" % (label, why)


def metric(summary: Dict[str, Any], name: str, stat: str, default: Any = 0) -> Any:
    return summary.get("metrics", {}).get(name, {}).get(stat, default)


def read_json(path: Path, default: Any) -> Any:
    if not path.exists():
        return default
    with path.open("r", encoding="utf-8") as handle:
        return json.load(handle)


def load_checks(path: Path) -> List[Dict[str, Any]]:
    data = read_json(path, [])
    if isinstance(data, dict):
        data = data.get("checks", [])
    return data


def write_result(run_dir: Path, stage: str, env: str) -> int:
    config = read_json(run_dir / "config.json", {})
    summary = read_json(run_dir / "k6-summary.json", {})
    checks = load_checks(run_dir / "checks.json")
    counts = read_json(run_dir / "counts.json", {})

    gen_cpu = float(config.get("generator_cpu_pct", 0.0))
    mock_value = config.get("mock_added_p99_ms")
    mock_added = None if mock_value is None else float(mock_value)
    completed_metric = counts.get("metric")
    db_count = counts.get("db")

    run_validity = validity(
        summary,
        gen_cpu,
        mock_added,
        dirty_tree=bool(config.get("git_dirty", False)),
        config_recorded=bool(config.get("config_recorded", True)),
        completed_metric=completed_metric,
        db_count=db_count,
    )

    platform_overhead_p99 = max(
        0.0,
        float(metric(summary, "http_req_duration", "p(99)", 0.0))
        - float(config.get("psp_latency_ms", 0.0)),
    )
    platform_overhead_p50 = max(
        0.0,
        float(metric(summary, "http_req_duration", "p(50)", 0.0))
        - float(config.get("psp_latency_ms", 0.0)),
    )
    unexpected_rate = float(metric(summary, "op_unexpected_error", "rate", 0.0)) * 100.0
    gate_result = gates(
        p99=platform_overhead_p99,
        p50=platform_overhead_p50,
        errors=unexpected_rate,
        checks=checks,
    )
    missing = required_checks(stage) - {check.get("check") for check in checks}
    if missing:
        gate_result = Gates(
            False,
            gate_result.reasons + ["missing checks: " + ", ".join(sorted(missing))],
        )

    lines = [render_header(stage, env).rstrip(), ""]
    lines.append("Run ID: %s  " % config.get("run_id", run_dir.name))
    lines.append(
        "Date / commit / configuration: %s / %s / %s"
        % (
            config.get("started_at", "?"),
            config.get("commit", "?"),
            config.get("step", "?"),
        )
    )
    lines.append(
        "Step and load profile: step %s, %s req/s, %s s"
        % (
            config.get("step", "?"),
            config.get("rate", "?"),
            config.get("duration_seconds", "?"),
        )
    )
    lines.append(
        "Instances: API %s, DB %s, mock %s"
        % (
            config.get("api_instances", "local"),
            config.get("db_instances", "local"),
            config.get("mock_instances", "1"),
        )
    )
    lines.append("")
    lines.append(
        "Achieved: admitted req/s %.2f  rejected req/s %.2f  completed payments/s %.2f"
        % (
            float(metric(summary, "http_reqs", "rate", 0.0)),
            float(metric(summary, "http_req_failed", "rate", 0.0)),
            float(metric(summary, "k6_payments_completed_total", "rate", 0.0)),
        )
    )
    lines.append(
        "Platform overhead latency: p50 %.2f ms  p95 %.2f ms  p99 %.2f ms"
        % (
            platform_overhead_p50,
            max(
                0.0,
                float(metric(summary, "http_req_duration", "p(95)", 0.0))
                - float(config.get("psp_latency_ms", 0.0)),
            ),
            platform_overhead_p99,
        )
    )
    lines.append(
        "End-to-end latency (PSP fixed at %s ms): p50 %.2f ms  p95 %.2f ms  p99 %.2f ms"
        % (
            config.get("psp_latency_ms", 0),
            float(metric(summary, "http_req_duration", "p(50)", 0.0)),
            float(metric(summary, "http_req_duration", "p(95)", 0.0)),
            float(metric(summary, "http_req_duration", "p(99)", 0.0)),
        )
    )
    lines.append("Unexpected error rate: %.3f%%" % unexpected_rate)
    lines.append("")
    lines.append(
        "Resources: API CPU %s%%, memory %s | DB CPU %s, I/O %s | generator CPU %.1f%%"
        % (
            config.get("api_cpu_pct", "n/a"),
            config.get("api_memory", "n/a"),
            config.get("db_cpu_pct", "n/a"),
            config.get("db_io", "n/a"),
            gen_cpu,
        )
    )
    lines.append(
        "Runtime: ThreadPool queue %s, Gen 2 GC/s %s, allocation rate %s"
        % (
            config.get("threadpool_queue", "n/a"),
            config.get("gen2_gc_per_s", "n/a"),
            config.get("allocation_rate", "n/a"),
        )
    )
    lines.append(
        "Database: pool wait %s, lock waits %s, deadlocks %s, WAL rate %s"
        % (
            config.get("pool_wait", "n/a"),
            config.get("lock_waits", "n/a"),
            config.get("deadlocks", "n/a"),
            config.get("wal_rate", "n/a"),
        )
    )
    lines.append(
        "Async: outbox age %s, consumer lag %s"
        % (config.get("outbox_age", "n/a"), config.get("consumer_lag", "n/a"))
    )
    lines.append("")
    lines.append("Correctness: " + " ".join(render(check) for check in checks))
    lines.append("")
    lines.append("Gates passed: %s" % ("yes" if gate_result.passed else "no"))
    lines.append("Run valid: %s" % ("yes" if run_validity.valid else "no"))
    if run_validity.reasons:
        lines.append("Invalid because: " + "; ".join(run_validity.reasons))
    if run_validity.not_checked:
        lines.append("Validity not checked: " + "; ".join(run_validity.not_checked))
    if gate_result.reasons:
        lines.append("Gate reasons: " + "; ".join(gate_result.reasons))
    lines.append("")
    lines.append("Bottleneck found: %s" % config.get("bottleneck", "not recorded"))
    lines.append("Action taken: %s" % config.get("action", "not recorded"))
    lines.append("Evidence link: %s" % config.get("evidence", "not recorded"))
    lines.append("Next step: %s" % config.get("next_step", "not recorded"))
    lines.append("")

    (run_dir / "result.md").write_text("\n".join(lines), encoding="utf-8")

    failed_checks = [
        check
        for check in checks
        if check.get("applicable", False) and not check.get("passed", False)
    ]
    return 0 if run_validity.valid and gate_result.passed and not failed_checks else 1


def main(argv: Optional[List[str]] = None) -> int:
    parser = argparse.ArgumentParser(description="Render a harness run into result.md")
    parser.add_argument("--run-dir", required=True)
    parser.add_argument("--stage", default="phase0")
    parser.add_argument("--env", default="local")
    args = parser.parse_args(argv)
    return write_result(Path(args.run_dir), args.stage, args.env)


if __name__ == "__main__":
    sys.exit(main())
