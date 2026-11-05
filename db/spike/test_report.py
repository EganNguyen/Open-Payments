import json
import pathlib

import report


def row(target, cpu, achieved=None, verify_passed=True, valid=True):
    return {
        "variant": "V",
        "target_tps": target,
        "achieved_tps": target if achieved is None else achieved,
        "primary_cpu_pct": cpu,
        "verify_passed": verify_passed,
        "valid": valid,
    }


def rows(*pairs, verify_passed=True):
    return [row(pairs[i], pairs[i + 1], verify_passed=verify_passed) for i in range(0, len(pairs), 2)]


def test_highest_rate_requires_cpu_gate():
    # 12k at 55% and 15k at 72%: the answer is 12k, since 60% is the limit
    assert report.sustained_rate(rows(12000, 55, 15000, 72)) == 12000


def test_failed_verify_disqualifies_row():
    assert report.sustained_rate(rows(20000, 40, verify_passed=False)) == 0


def test_invalid_row_is_disqualified():
    assert report.sustained_rate([row(20000, 40, valid=False)]) == 0


def test_rate_it_could_not_keep_up_with_is_disqualified():
    # asked for 10k, delivered 8k: an open-model run that fell behind doesn't count
    assert report.sustained_rate([row(10000, 40, achieved=8000), row(5000, 30)]) == 5000


def test_missing_cpu_measurement_is_disqualified():
    # no samples must never read as 0% and pass the CPU gate
    assert report.sustained_rate([row(20000, None)]) == 0


def test_linux_rates_can_reach_required_commits_on_every_mix():
    # otherwise a variant could never reach the target, whatever the database can do
    matrix = json.loads((pathlib.Path(__file__).parent / "matrix.json").read_text())
    required = report.required_commits(**matrix["required"])
    top = max(matrix["environments"]["linux"]["rates"])
    for variant in matrix["variants"]:
        scripts = variant.get("set", {}).get("scripts", matrix["baseline"]["scripts"])
        assert top * report.commits_per_script(scripts) >= required, variant["name"]


def test_required_commits_two_call():
    # 5,000 payments at 4 commits, 833 refunds at 2, no callbacks
    assert report.required_commits(5000, 4, 833, 0) == 21666


def test_required_commits_sale_with_callbacks():
    # 5,000 sales at 2 commits, 833 refunds at 2, one callback commit per operation
    assert report.required_commits(5000, 2, 833, 5833) == 17499


def test_commits_per_script_is_weighted():
    assert report.commits_per_script({"sale": 3, "callback": 1}) == (3 * 2 + 1 * 1) / 4


def test_generator_over_80_percent_invalidates():
    assert report.is_valid(generator_cpu_pct=81, failed=0, aborted=False) is False


def test_failed_transactions_invalidate():
    assert report.is_valid(generator_cpu_pct=10, failed=1, aborted=False) is False


def test_aborted_client_invalidates():
    assert report.is_valid(generator_cpu_pct=10, failed=0, aborted=True) is False


def test_clean_run_is_valid():
    assert report.is_valid(generator_cpu_pct=80, failed=0, aborted=False) is True


def test_percentile():
    assert report.percentile(list(range(1, 101)), 99) == 99
    assert report.percentile(list(range(1, 101)), 50) == 50
    assert report.percentile([], 99) is None


def test_log_latency_includes_schedule_lag():
    # client tx time script epoch us lag: open-model latency counts time spent waiting to start
    lines = ["0 1 2000 0 1700000000 123 3000", "0 2 1000 0 1700000000 456 0"]
    assert report.latencies_ms(lines) == [5.0, 1.0]


def test_parse_summary():
    text = "tps = 123.4 (without initial connection time)\nnumber of failed transactions: 2 (0.1%)\n"
    assert report.parse_summary(text) == {"tps": 123.4, "failed": 2, "aborted": False}


def test_parse_summary_detects_aborted_client():
    assert report.parse_summary("pgbench: error: client 3 script 0 aborted in command 1\n")["aborted"] is True


def test_local_summary_says_comparative_only():
    assert "Local: comparative only, does not answer G1" in report.render_summary([row(100, 5)], "local", 100)


def test_linux_summary_has_no_local_warning():
    assert "comparative only" not in report.render_summary([row(100, 5)], "linux", 100)


def test_summary_marks_invalid_rows():
    assert "invalid" in report.render_summary([row(100, 5, valid=False)], "linux", 100)
