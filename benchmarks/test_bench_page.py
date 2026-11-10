"""Tests for bench_page.py: the benchmark results page is built only from run files."""

import json
from pathlib import Path

import pytest

import bench_page as bp

STEP5 = """# Run result: preview

Run ID: 20261109T045857Z-e4bff52-s5-47d3
Date / commit / configuration: 2026-11-09T04:58:57Z / e4bff52 / 5
Step and load profile: step 5, 500 req/s, 600 s
Instances: API local, DB local, mock 1

Achieved: admitted req/s 491.98  rejected req/s 0.00  completed payments/s 248.32
Platform overhead latency: p50 6.07 ms  p95 12.07 ms  p99 17.74 ms
Unexpected error rate: 0.000%

Resources: API CPU 117.88%, memory 184827904 bytes | DB CPU 0.14, I/O 621MB / 8.79GB | generator CPU 1.3%
Database: pool wait 0.0 s, lock waits 126.0 locks held (pg_locks_count; proxy for lock waits), deadlocks 0.0, WAL rate 1557900 bytes/s
Correctness: CK1 pass CK2 pass CK3 pass CK4 pass CK5 pass CK6 pass CK7 n/a (not applicable) CK8 n/a (not applicable) CK9 pass CK10 pass

Gates passed: yes
Run valid: yes
"""


def result_md(step=5, target=500, completed=248.32, p50=6.07, p99=17.74, error=0.0,
              gates=True, valid=True, invalid="", reasons="", ck5="pass"):
    text = (STEP5.replace("step 5, 500 req/s", f"step {step}, {target} req/s")
            .replace("completed payments/s 248.32", f"completed payments/s {completed:.2f}")
            .replace("p50 6.07 ms", f"p50 {p50:.2f} ms")
            .replace("p99 17.74 ms", f"p99 {p99:.2f} ms")
            .replace("Unexpected error rate: 0.000%", f"Unexpected error rate: {error:.3f}%")
            .replace("CK5 pass", f"CK5 {ck5}")
            .replace("Gates passed: yes", f"Gates passed: {'yes' if gates else 'no'}")
            .replace("Run valid: yes", f"Run valid: {'yes' if valid else 'no'}"))
    if invalid:
        text += f"Invalid because: {invalid}\n"
    if reasons:
        text += f"Gate reasons: {reasons}\n"
    return text


def make_run(runs_dir: Path, run_id: str, text: str) -> None:
    (runs_dir / run_id).mkdir(parents=True)
    (runs_dir / run_id / "result.md").write_text(text)


def config(runs, static_gates=None):
    return {
        "milestone_completed_per_second": 500,
        "milestone_runs": 3,
        "thresholds": {"p50_ms": 25, "p99_ms": 100, "error_rate_pct": 0.1},
        "runs": runs,
        "static_gates": static_gates or [],
        "correctness_requires": None,
        "d56": [],
    }


# --- parsing ---------------------------------------------------------------

def test_parses_a_recorded_result():
    r = bp.parse_result_md(STEP5)
    assert r["step"] == 5 and r["target"] == 500 and r["duration_s"] == 600
    assert r["admitted"] == 491.98 and r["completed"] == 248.32
    assert (r["p50"], r["p95"], r["p99"]) == (6.07, 12.07, 17.74)
    assert r["error_pct"] == 0.0 and r["api_cpu_pct"] == 117.88
    assert r["pool_wait_s"] == 0.0 and r["deadlocks"] == 0.0
    assert r["gates_passed"] is True and r["valid"] is True
    assert r["checks"]["CK1"] == "pass" and r["checks"]["CK7"] == "n/a"
    assert r["commit"] == "e4bff52" and r["started_at"] == "2026-11-09T04:58:57Z"


def test_missing_lines_are_none_never_zero():
    r = bp.parse_result_md("Run ID: x\nRun valid: no\n")
    assert r["completed"] is None and r["p99"] is None and r["error_pct"] is None
    assert r["gates_passed"] is None and r["valid"] is False and r["checks"] == {}


def test_invalid_reasons_split_on_semicolons():
    r = bp.parse_result_md(result_md(valid=False, invalid="generator dropped 5 arrivals; completed metric 1 differs from database 2"))
    assert r["invalid_because"] == ["generator dropped 5 arrivals", "completed metric 1 differs from database 2"]


# --- whole payments --------------------------------------------------------

@pytest.mark.parametrize("rate,shown", [(248.32, 248), (499.99, 499), (500.03, 500), (0.64, 0), (None, None)])
def test_completed_rate_shows_whole_payments_rounded_down(rate, shown):
    assert bp.whole_payments(rate) == shown


# --- headline and runs -----------------------------------------------------

def test_headline_is_the_highest_valid_run_whose_gates_passed(tmp_path):
    make_run(tmp_path, "a-s5", result_md())
    make_run(tmp_path, "b-s6", result_md(step=6, target=1000, completed=500.03, gates=True, valid=False, invalid="generator dropped 68 arrivals"))
    make_run(tmp_path, "c-s6", result_md(step=6, target=1000, completed=496.0, p99=588.0, gates=False, valid=True))
    page = bp.build(config([{"id": "a-s5"}, {"id": "b-s6"}, {"id": "c-s6"}]), tmp_path, "abc1234", "2026-11-10")
    assert page["headline"]["run_id"] == "a-s5"
    assert page["headline"]["completed_whole"] == 248
    assert page["headline"]["completed_exact"] == 248.32


def test_no_valid_run_gives_no_headline(tmp_path):
    make_run(tmp_path, "b-s6", result_md(valid=False, invalid="dropped"))
    page = bp.build(config([{"id": "b-s6"}]), tmp_path, "abc1234", "2026-11-10")
    assert page["headline"] is None


def test_excluded_run_is_listed_but_never_used(tmp_path):
    make_run(tmp_path, "a-s5", result_md(completed=100.0))
    make_run(tmp_path, "x-s6", result_md(step=6, target=1000, completed=900.0))
    page = bp.build(config([{"id": "a-s5"}, {"id": "x-s6", "exclude": "sent no API token"}]), tmp_path, "abc1234", "2026-11-10")
    assert page["headline"]["run_id"] == "a-s5"
    x = next(r for r in page["runs"] if r["run_id"] == "x-s6")
    assert x["status"] == "excluded" and x["exclude_reason"] == "sent no API token"


def test_missing_run_directory_is_shown_as_missing_evidence(tmp_path):
    page = bp.build(config([{"id": "gone-s6"}]), tmp_path, "abc1234", "2026-11-10")
    run = page["runs"][0]
    assert run["status"] == "missing" and run["completed"] is None


# --- gates and verdict -----------------------------------------------------

def gate(page, gate_id):
    return next(g for g in page["gates"] if g["id"] == gate_id)


def test_throughput_needs_three_valid_runs_at_the_milestone(tmp_path):
    ids = []
    for i, c in enumerate([505.0, 502.0]):
        make_run(tmp_path, f"r{i}", result_md(step=6, target=1000, completed=c))
        ids.append({"id": f"r{i}"})
    page = bp.build(config(ids), tmp_path, "abc1234", "2026-11-10")
    assert gate(page, "throughput")["status"] == "INCONCLUSIVE"
    assert "2 of 3" in gate(page, "throughput")["evidence"]
    make_run(tmp_path, "r2", result_md(step=6, target=1000, completed=501.0))
    page = bp.build(config(ids + [{"id": "r2"}]), tmp_path, "abc1234", "2026-11-10")
    assert gate(page, "throughput")["status"] == "PASS"


def test_three_valid_target_runs_with_one_short_fail_throughput(tmp_path):
    ids = []
    for i, c in enumerate([505.0, 502.0, 498.0]):
        make_run(tmp_path, f"r{i}", result_md(step=6, target=1000, completed=c))
        ids.append({"id": f"r{i}"})
    page = bp.build(config(ids), tmp_path, "abc1234", "2026-11-10")
    assert gate(page, "throughput")["status"] == "FAIL"


def test_latency_breach_in_a_valid_run_fails_the_gate(tmp_path):
    make_run(tmp_path, "a", result_md(p99=130.0, gates=False))
    page = bp.build(config([{"id": "a"}]), tmp_path, "abc1234", "2026-11-10")
    assert gate(page, "platform_overhead")["status"] == "FAIL"


def test_latency_breach_only_in_an_invalid_run_is_inconclusive(tmp_path):
    make_run(tmp_path, "a", result_md())
    make_run(tmp_path, "b", result_md(step=6, target=1000, p99=173.47, valid=False, invalid="count mismatch"))
    page = bp.build(config([{"id": "a"}, {"id": "b"}]), tmp_path, "abc1234", "2026-11-10")
    assert gate(page, "platform_overhead")["status"] == "INCONCLUSIVE"


def test_a_correctness_failure_makes_the_verdict_fail_at_rule_1(tmp_path):
    make_run(tmp_path, "a", result_md(ck5="FAIL"))
    make_run(tmp_path, "b", result_md(valid=False, invalid="dropped"))
    page = bp.build(config([{"id": "a"}, {"id": "b"}]), tmp_path, "abc1234", "2026-11-10")
    assert page["verdict"] == {"verdict": "FAIL", "rule": 1, "triggers": ["correctness"]}


def test_an_inconclusive_gate_stops_the_verdict_at_rule_2(tmp_path):
    make_run(tmp_path, "a", result_md())
    static = [{"id": "tx1", "name": "Forced deadlock", "kind": "reliability", "status": "INCONCLUSIVE", "evidence": "TX1 isn't built."}]
    page = bp.build(config([{"id": "a"}], static), tmp_path, "abc1234", "2026-11-10")
    assert page["verdict"]["verdict"] == "INCONCLUSIVE" and page["verdict"]["rule"] == 2
    assert "tx1" in page["verdict"]["triggers"]


def test_correctness_requirement_caps_a_pass(tmp_path):
    make_run(tmp_path, "a", result_md())
    cfg = config([{"id": "a"}])
    cfg["correctness_requires"] = "CK11 isn't built."
    page = bp.build(cfg, tmp_path, "abc1234", "2026-11-10")
    assert gate(page, "correctness")["status"] == "INCONCLUSIVE"
    assert "CK11 isn't built." in gate(page, "correctness")["evidence"]


# --- output ----------------------------------------------------------------

def test_render_injects_data_without_closing_the_script(tmp_path):
    template = '<script type="application/json" id="report-data">/*REPORT_DATA*/</script>'
    html = bp.render(template, {"note": "</script><b>x</b>"})
    assert html.count("</script>") == 1
    assert json.loads(html.split(">", 1)[1].rsplit("</script>", 1)[0]) == {"note": "</script><b>x</b>"}


def test_render_refuses_a_template_without_the_slot():
    with pytest.raises(ValueError):
        bp.render("<html></html>", {})
