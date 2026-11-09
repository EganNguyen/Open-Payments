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
        completed_metric=1000, db_count=1000,
        shares={
            "sale": 0.40, "authorize": 0.10, "capture": 0.10,
            "status": 0.15, "refund": 0.10, "retry": 0.10, "other": 0.05,
        })
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
    assert "Validity not checked: mock added p99 latency; mix shares; completed metric vs database count" in result
    assert "Run valid: no" in result


def test_empty_checks_fail_gates():
    assert gates(p99=10, errors=0, checks=[]).passed is False


def test_missing_required_check_fails_gates():
    from report import required_checks
    assert "CK6" in required_checks("phase1")
    assert required_checks("phase1") - {"CK1"} != set()


def test_share_miss_invalidates_run():
    result = validity(
        k6(), gen_cpu=40, mock_added_p99_ms=1.0,
        completed_metric=1000, db_count=1000,
        shares={
            "sale": 0.30, "authorize": 0.10, "capture": 0.10,
            "status": 0.15, "refund": 0.10, "retry": 0.10, "other": 0.05,
        }, two_call_share=0.2)
    assert result.valid is False
    assert any("sale" in reason for reason in result.reasons)


def test_share_within_two_points_passes():
    result = validity(
        k6(), gen_cpu=40, mock_added_p99_ms=1.0,
        completed_metric=1000, db_count=1000,
        shares={
            "sale": 0.39, "authorize": 0.10, "capture": 0.10,
            "status": 0.16, "refund": 0.10, "retry": 0.10, "other": 0.05,
        }, two_call_share=0.2)
    assert result.valid is True


def test_missing_shares_are_not_checked():
    result = validity(
        k6(), gen_cpu=40, mock_added_p99_ms=1.0,
        completed_metric=1000, db_count=1000)
    assert result.valid is False
    assert any("mix shares" in reason for reason in result.not_checked)


def function_body(source, name):
    start = source.index("function " + name + "(")
    brace = source.index("{", start)
    depth = 0
    for index in range(brace, len(source)):
        if source[index] == "{":
            depth += 1
        elif source[index] == "}":
            depth -= 1
            if depth == 0:
                return source[brace + 1:index]
    raise AssertionError("unterminated function " + name)


def test_other_does_not_take_from_the_capture_queue():
    from pathlib import Path
    source = (Path(__file__).parent / "k6" / "mix.js").read_text(encoding="utf-8")
    body = function_body(source, "other")
    assert "captureQueue" not in body
    assert "pickRecent(recent)" in body


def test_dynamic_urls_have_fixed_metric_names():
    from pathlib import Path
    source = (Path(__file__).parent / "k6" / "mix.js").read_text(encoding="utf-8")
    for name in (
        "POST /v1/payments/:id/capture",
        "GET /v1/payments/:id",
        "POST /v1/payments/:id/refunds",
        "POST /v1/payments/:id/cancel",
        "GET /inquire/:key",
    ):
        assert name in source


def test_result_lists_each_check_with_its_duration(tmp_path):
    (tmp_path / "config.json").write_text(json.dumps({
        "run_id": "durations",
        "commit": "abc",
        "step": 1,
        "rate": 10,
        "duration_seconds": 10,
        "started_at": "2026-11-06T00:00:00Z",
        "generator_cpu_pct": 1,
        "psp_latency_ms": 200,
    }), encoding="utf-8")
    (tmp_path / "k6-summary.json").write_text(json.dumps(k6()), encoding="utf-8")
    checks = [
        dict(ck("CK1", True), duration_ms=1234),
        dict(ck("CK4", False), duration_ms=353),
        ck("CK8", None),
    ]
    (tmp_path / "checks.json").write_text(json.dumps(checks), encoding="utf-8")

    write_result(tmp_path, "phase1", "local")
    result = (tmp_path / "result.md").read_text(encoding="utf-8")
    assert "| Check | Result | Duration |" in result
    assert "| CK1 | pass | 1,234 ms |" in result
    assert "| CK4 | FAIL | 353 ms |" in result
    assert "| CK8 | n/a | n/a |" in result


def test_checks_recorded_before_durations_render_na(tmp_path):
    from report import render_check_table
    assert render_check_table([ck("CK1", True)]) == [
        "| Check | Result | Duration |",
        "|---|---|---|",
        "| CK1 | pass | n/a |",
    ]


def _write_run(tmp_path, metrics):
    (tmp_path / "config.json").write_text(json.dumps({
        "run_id": "rates",
        "commit": "abc",
        "step": 0,
        "rate": 1,
        "duration_seconds": 300,
        "started_at": "2026-11-09T00:00:00Z",
        "generator_cpu_pct": 1,
        "psp_latency_ms": 200,
    }), encoding="utf-8")
    summary = k6()
    summary["metrics"].update(metrics)
    (tmp_path / "k6-summary.json").write_text(json.dumps(summary), encoding="utf-8")
    (tmp_path / "checks.json").write_text(json.dumps([ck("CK1", True)]), encoding="utf-8")
    write_result(tmp_path, "phase1", "local")
    return (tmp_path / "result.md").read_text(encoding="utf-8")


def test_unexpected_errors_come_from_the_rate_metric_value(tmp_path):
    # k6's summary export gives a Rate metric "value", not "rate". A run where every
    # request failed must show it and fail its gates.
    result = _write_run(tmp_path, {
        "op_unexpected_error": {"passes": 304, "fails": 0, "value": 1},
    })
    assert "Unexpected error rate: 100.000%" in result
    assert "Gates passed: no" in result


def test_rejected_rate_comes_from_the_rejection_counter(tmp_path):
    result = _write_run(tmp_path, {
        "http_reqs": {"count": 300, "rate": 1.0},
        "k6_requests_rejected_total": {"count": 30, "rate": 0.1},
        "http_req_failed": {"passes": 300, "fails": 0, "value": 1},
    })
    assert "rejected req/s 0.10" in result


STEP0_SHARES = {
    "sale": 0.411, "authorize": 0.100, "capture": 0.104,
    "status": 0.181, "refund": 0.068, "retry": 0.087, "other": 0.049,
}


def test_small_sample_share_within_three_sigma_passes():
    # E8: step 0 had 309 mix requests. Status 18.1% against 15% is 3.1 points, but
    # 3 binomial standard deviations at that size is 6.1 points.
    result = validity(
        k6(), gen_cpu=1, mock_added_p99_ms=1.0, completed_metric=1000, db_count=1000,
        shares=STEP0_SHARES, share_requests=309, two_call_share=0.2)
    assert result.valid is True, result.reasons


def test_large_sample_keeps_the_two_point_floor():
    # At 100,000 requests 3 sigma is under 2 points, so the D47 2-point limit applies.
    result = validity(
        k6(), gen_cpu=1, mock_added_p99_ms=1.0, completed_metric=1000, db_count=1000,
        shares=STEP0_SHARES, share_requests=100000, two_call_share=0.2)
    assert result.valid is False
    assert any("status share" in reason for reason in result.reasons)


def test_three_sigma_still_catches_a_real_miss():
    shares = dict(STEP0_SHARES, sale=0.30)
    result = validity(
        k6(), gen_cpu=1, mock_added_p99_ms=1.0, completed_metric=1000, db_count=1000,
        shares=shares, share_requests=309, two_call_share=0.2)
    assert result.valid is False
    assert any("sale share" in reason for reason in result.reasons)


def test_share_limit_shown_with_a_miss():
    result = validity(
        k6(), gen_cpu=1, mock_added_p99_ms=1.0, completed_metric=1000, db_count=1000,
        shares=dict(STEP0_SHARES, sale=0.30), share_requests=309, two_call_share=0.2)
    assert any("limit 8.4 points" in reason for reason in result.reasons), result.reasons
