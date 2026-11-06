import json

from report import (
    ck,
    gates,
    reconcile_counts,
    render,
    render_header,
    validity,
    write_result,
)


def k6(dropped=0, completed=1000, iterations=1000):
    return {
        "metrics": {
            "dropped_iterations": {"count": dropped},
            "iterations": {"count": iterations},
            "op_payments_completed_total": {"count": completed},
        }
    }


def test_dropped_iterations_invalidates_run():
    assert validity(k6(dropped=1), gen_cpu=40).valid is False


def test_generator_cpu_over_80_invalidates_run():
    assert validity(k6(), gen_cpu=81).valid is False


def test_mock_added_latency_invalidates_run():
    assert validity(k6(), gen_cpu=40, mock_added_p99_ms=20).valid is False


def test_failed_ck_fails_gates_even_if_fast():
    assert gates(p99=10, errors=0, checks=[ck("CK1", False)]).passed is False


def test_not_applicable_ck_is_shown_not_passed():
    assert "n/a" in render(ck("CK4", None))


def test_counter_must_match_database():
    # completed metric 1000, database shows 990: run fails on a measurement error
    assert reconcile_counts(metric=1000, db=990).ok is False


def test_preview_label_before_phase3():
    assert "preview" in render_header(stage="phase1")


def test_missing_mock_added_latency_is_not_checked():
    result = validity(k6(), gen_cpu=40, mock_added_p99_ms=None)
    assert result.valid is False
    assert any("mock added" in reason for reason in result.not_checked)


def test_missing_completed_count_reconciliation_is_not_checked():
    result = validity(k6(), gen_cpu=40, completed_metric=None, db_count=None)
    assert result.valid is False
    assert any("completed" in reason for reason in result.not_checked)


def test_checked_validity_rules_pass():
    result = validity(
        k6(), gen_cpu=40, mock_added_p99_ms=1.0,
        completed_metric=1000, db_count=1000)
    assert result.valid is True
    assert result.not_checked == []


def test_result_marks_unchecked_rules(tmp_path):
    (tmp_path / "config.json").write_text(json.dumps({
        "run_id": "unchecked",
        "commit": "abc",
        "step": 1,
        "rate": 10,
        "duration_seconds": 10,
        "started_at": "2026-11-06T00:00:00Z",
        "generator_cpu_pct": 1,
        "psp_latency_ms": 200,
        "mock_added_p99_ms": None,
    }), encoding="utf-8")
    (tmp_path / "k6-summary.json").write_text(json.dumps(k6()), encoding="utf-8")
    (tmp_path / "checks.json").write_text("[]", encoding="utf-8")

    assert write_result(tmp_path, "phase1", "local") == 1
    result = (tmp_path / "result.md").read_text(encoding="utf-8")
    assert "Validity not checked: mock added p99 latency; completed metric vs database count" in result
    assert "Run valid: no" in result


def test_empty_checks_fail_gates():
    assert gates(p99=10, errors=0, checks=[]).passed is False


def test_missing_required_check_fails_gates():
    from report import required_checks
    assert "CK6" in required_checks("phase1")
    assert required_checks("phase1") - {"CK1"} != set()
