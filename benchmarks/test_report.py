from report import (
    ck,
    gates,
    reconcile_counts,
    render,
    render_header,
    validity,
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
