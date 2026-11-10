#!/usr/bin/env python3
"""Build the OP-BENCH Results page from recorded run files.

Every number on the page comes from a run's own result.md. The run list,
exclusion reasons and the gates that have no run data yet live in
benchmarks/report-runs.json. Nothing here is typed by hand into the page.

    .venv/bin/python benchmarks/bench_page.py --out benchmarks/site/index.html

Interim until op-bench writes suite.html (plan P7b).
"""

from __future__ import annotations

import argparse
import json
import math
import re
import subprocess
import sys
from datetime import date
from pathlib import Path
from typing import Any, Dict, List, Optional

ROOT = Path(__file__).resolve().parent.parent
SLOT = "/*REPORT_DATA*/"

COMPUTED_GATES = {
    "correctness": ("Correctness", "All applicable CK checks pass in every run", "correctness", "op-checks · checks in result.md"),
    "duplicates": ("Duplicates, missing, partial", "0, from CK1, CK2 and CK9", "correctness", "CK1 CK2 CK9"),
    "throughput": ("Throughput", "≥ {m} completed payments/s in each of {n} valid runs", "performance", "DB count · result.md"),
    "platform_overhead": ("Platform overhead", "p50 ≤ {p50} ms, p99 ≤ {p99} ms", "performance", "op_platform_overhead_seconds"),
    "error_rate": ("Error rate", "< {e}%, excluding 402, 409, 429, 503", "performance", "k6 + API"),
    "run_validity": ("Run validity", "No dropped arrivals, generator CPU ≤ 80%, mix within bounds", "validity", "report.py"),
}


# --- parsing ---------------------------------------------------------------

def _num(pattern: str, text: str) -> Optional[float]:
    m = re.search(pattern, text)
    return float(m.group(1).replace(",", "")) if m else None


def _line(label: str, text: str) -> Optional[str]:
    m = re.search(rf"^{re.escape(label)}:\s*(.*?)\s*$", text, re.M)
    return m.group(1) if m else None


def _yes_no(value: Optional[str]) -> Optional[bool]:
    if value is None:
        return None
    return value.strip().lower().startswith("yes")


def _split(value: Optional[str]) -> List[str]:
    return [p.strip() for p in value.split(";") if p.strip()] if value else []


def parse_result_md(text: str) -> Dict[str, Any]:
    """Read the fields the page needs from a run's result.md. A missing line is None."""
    step = re.search(r"Step and load profile: step (\d+), ([\d,.]+) req/s, (\d+) s", text)
    when = re.search(r"Date / commit / configuration: (\S+) / (\w+)", text)
    checks = {k: v for k, v in re.findall(r"\b(CK\d+) (pass|FAIL|n/a)", _line("Correctness", text) or "")}
    valid = _yes_no(_line("Run valid", text))
    return {
        "step": int(step.group(1)) if step else None,
        "target": float(step.group(2).replace(",", "")) if step else None,
        "duration_s": int(step.group(3)) if step else None,
        "started_at": when.group(1) if when else None,
        "commit": when.group(2) if when else None,
        "admitted": _num(r"admitted req/s ([\d,.]+)", text),
        "completed": _num(r"completed payments/s ([\d,.]+)", text),
        "p50": _num(r"Platform overhead latency: p50 ([\d,.]+) ms", text),
        "p95": _num(r"Platform overhead latency:.*?p95 ([\d,.]+) ms", text),
        "p99": _num(r"Platform overhead latency:.*?p99 ([\d,.]+) ms", text),
        "error_pct": _num(r"Unexpected error rate: ([\d,.]+)%", text),
        "api_cpu_pct": _num(r"API CPU ([\d,.]+)%", text),
        "pool_wait_s": _num(r"pool wait ([\d,.]+) s", text),
        "deadlocks": _num(r"deadlocks ([\d,.]+)", text),
        "gates_passed": _yes_no(_line("Gates passed", text)),
        "valid": bool(valid),
        "invalid_because": _split(_line("Invalid because", text)),
        "gate_reasons": _split(_line("Gate reasons", text)),
        "checks": checks,
    }


def read_arrivals(path: Path) -> Dict[str, Optional[int]]:
    """Arrivals k6 sent (its completed iterations) and arrivals it dropped, from k6-summary.json."""
    if not path.exists():
        return {"arrivals": None, "dropped": None}
    metrics = json.loads(path.read_text()).get("metrics", {})
    sent = metrics.get("iterations", {}).get("count")
    dropped = metrics.get("dropped_iterations", {}).get("count", 0)
    return {"arrivals": int(sent) if sent is not None else None, "dropped": int(dropped)}


def whole_payments(rate: Optional[float]) -> Optional[int]:
    """Completed payments/s as whole payments, rounded down so the page never overstates."""
    return None if rate is None else math.floor(rate + 1e-9)


# --- runs ------------------------------------------------------------------

def load_runs(cfg: Dict[str, Any], runs_dir: Path) -> List[Dict[str, Any]]:
    runs = []
    for entry in cfg["runs"]:
        run_id = entry["id"]
        path = runs_dir / run_id / "result.md"
        if path.exists():
            run = parse_result_md(path.read_text())
            run.update(read_arrivals(runs_dir / run_id / "k6-summary.json"))
            status = "excluded" if entry.get("exclude") else "included"
        else:
            run = parse_result_md("")
            run["valid"] = None
            status = "missing"
        run.update({
            "run_id": run_id,
            "short_id": "-".join(run_id.split("-")[-2:]),
            "status": status,
            "exclude_reason": entry.get("exclude"),
            "note": entry.get("note"),
            "completed_whole": whole_payments(run["completed"]),
            "result_path": f"benchmarks/runs/{run_id}/result.md",
        })
        runs.append(run)
    return runs


def _passing(run: Dict[str, Any]) -> bool:
    return run["valid"] is True and run["gates_passed"] is True


def headline(runs: List[Dict[str, Any]], cfg: Dict[str, Any]) -> Optional[Dict[str, Any]]:
    candidates = [r for r in runs if r["status"] == "included" and _passing(r) and r["completed"] is not None]
    if not candidates:
        return None
    best = max(candidates, key=lambda r: (r["completed"], r["target"] or 0))
    return _headline_fields(best, cfg)


def _headline_fields(best: Dict[str, Any], cfg: Dict[str, Any]) -> Dict[str, Any]:
    applicable = {k: v for k, v in best["checks"].items() if v != "n/a"}
    milestone = cfg["milestone_completed_per_second"]
    return {
        "run_id": best["run_id"], "short_id": best["short_id"], "step": best["step"],
        "target": best["target"], "admitted": best["admitted"],
        "completed_exact": best["completed"], "completed_whole": whole_payments(best["completed"]),
        "milestone": milestone, "milestone_pct": round(100 * best["completed"] / milestone, 1),
        "p50": best["p50"], "p99": best["p99"], "error_pct": best["error_pct"],
        "api_cpu_pct": best["api_cpu_pct"], "pool_wait_s": best["pool_wait_s"], "deadlocks": best["deadlocks"],
        "checks_passed": sum(1 for v in applicable.values() if v == "pass"),
        "checks_applicable": len(applicable),
        "checks_passing": [k for k, v in best["checks"].items() if v == "pass"],
        "checks_na": [k for k, v in best["checks"].items() if v == "n/a"],
    }


def results(runs: List[Dict[str, Any]], cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    """The first view's result cards, named in report-runs.json "results". Each card's numbers
    come from its run; "compare" adds the same rate held for longer, whatever its outcome."""
    by_id = {r["run_id"]: r for r in runs}
    cards = []
    for entry in cfg.get("results", []):
        run = by_id.get(entry["run"])
        if run is None or run["status"] == "missing":
            raise SystemExit(f"result card run {entry['run']} has no result.md")
        card = _headline_fields(run, cfg)
        card.update({k: entry.get(k) for k in ("title", "note", "ruling")})
        card.update({"duration_s": run["duration_s"], "arrivals": run.get("arrivals"),
                     "dropped": run.get("dropped"), "valid": run["valid"] is True,
                     "gates_passed": run["gates_passed"] is True, "p95": run["p95"]})
        if "compare" in entry:
            other = by_id.get(entry["compare"]["run"])
            if other is None or other["status"] == "missing":
                raise SystemExit(f"compare run {entry['compare']['run']} has no result.md")
            card["compare"] = {
                "label": entry["compare"].get("label"), "run_id": other["run_id"], "short_id": other["short_id"],
                "target": other["target"], "duration_s": other["duration_s"], "arrivals": other.get("arrivals"),
                "completed_exact": other["completed"], "p99": other["p99"], "gates_passed": other["gates_passed"] is True,
                "valid": other["valid"] is True, "gate_reasons": other["gate_reasons"], "note": entry["compare"].get("note"),
            }
        cards.append(card)
    return cards


# --- gates -----------------------------------------------------------------

def _gate(gate_id: str, cfg: Dict[str, Any], status: str, evidence: str, runs: List[str]) -> Dict[str, Any]:
    name, req, kind, source = COMPUTED_GATES[gate_id]
    t = cfg["thresholds"]
    req = req.format(m=cfg["milestone_completed_per_second"], n=cfg["milestone_runs"],
                     p50=_fmt(t["p50_ms"]), p99=_fmt(t["p99_ms"]), e=_fmt(t["error_rate_pct"]))
    return {"id": gate_id, "name": name, "requirement": req, "kind": kind, "status": status,
            "evidence": evidence, "source": source, "runs": runs}


def _fmt(x: float) -> str:
    return f"{x:g}"


def _ms(x: Optional[float]) -> str:
    return "not measured" if x is None else f"{x:,.2f} ms"


def compute_gates(runs: List[Dict[str, Any]], cfg: Dict[str, Any]) -> List[Dict[str, Any]]:
    used = [r for r in runs if r["status"] == "included"]
    valid = [r for r in used if r["valid"]]
    invalid = [r for r in used if not r["valid"]]
    t = cfg["thresholds"]
    gates = []

    # Correctness and duplicates
    failed = [(r, k) for r in used for k, v in r["checks"].items() if v == "FAIL"]
    if failed:
        gates.append(_gate("correctness", cfg, "FAIL",
                           "; ".join(f"{k} failed in {r['short_id']}" for r, k in failed),
                           sorted({r["run_id"] for r, _ in failed})))
    elif not used:
        gates.append(_gate("correctness", cfg, "INCONCLUSIVE", "No run in this report.", []))
    else:
        passing = sorted({k for r in used for k, v in r["checks"].items() if v == "pass"}, key=lambda k: int(k[2:]))
        na = sorted({k for r in used for k, v in r["checks"].items() if v == "n/a"}, key=lambda k: int(k[2:]))
        text = f"{', '.join(passing)} pass in all {len(used)} runs." + (f" {', '.join(na)} n/a." if na else "")
        status = "PASS"
        if cfg.get("correctness_requires"):
            status, text = "INCONCLUSIVE", f"{text} {cfg['correctness_requires']}"
        gates.append(_gate("correctness", cfg, status, text, [r["run_id"] for r in used]))

    dup = [(r, k) for r in used for k in ("CK1", "CK2", "CK9") if r["checks"].get(k) == "FAIL"]
    gates.append(_gate("duplicates", cfg, "FAIL" if dup else ("PASS" if used else "INCONCLUSIVE"),
                       "; ".join(f"{k} failed in {r['short_id']}" for r, k in dup) if dup
                       else f"CK1, CK2 and CK9 pass in all {len(used)} runs.",
                       [r["run_id"] for r in used]))

    # Throughput
    m, need = cfg["milestone_completed_per_second"], cfg["milestone_runs"]
    at_target = [r for r in valid if (r["target"] or 0) >= cfg.get("milestone_target_rps", 2 * m)]
    reached = [r for r in at_target if r["gates_passed"] and (r["completed"] or 0) >= m]
    best_valid = max((r for r in valid if r["gates_passed"] and r["completed"] is not None),
                     key=lambda r: r["completed"], default=None)
    best_any = max((r for r in used if r["completed"] is not None), key=lambda r: r["completed"], default=None)
    text = f"{len(reached)} of {need} valid runs reached {m} completed payments/s."
    if best_valid:
        text += f" Best valid: {whole_payments(best_valid['completed'])}/s at step {best_valid['step']}."
    if best_any and best_any is not best_valid and not best_any["valid"]:
        text += f" Best in any run: {whole_payments(best_any['completed'])}/s, in an invalid run."
    if len(reached) >= need:
        status = "PASS"
    elif len(at_target) >= need:
        status = "FAIL"
    else:
        status = "INCONCLUSIVE"
    gates.append(_gate("throughput", cfg, status, text, [r["run_id"] for r in reached]))

    # Platform overhead
    def breach(r):
        return (r["p50"] is not None and r["p50"] > t["p50_ms"]) or (r["p99"] is not None and r["p99"] > t["p99_ms"])
    valid_breach = [r for r in valid if breach(r)]
    top_valid = max(valid, key=lambda r: r["target"] or 0, default=None)
    beyond = [r for r in invalid if top_valid is None or (r["target"] or 0) > (top_valid["target"] or 0)]
    if valid_breach:
        r = max(valid_breach, key=lambda r: r["p99"] or 0)
        gates.append(_gate("platform_overhead", cfg, "FAIL",
                           f"p50 {_ms(r['p50'])}, p99 {_ms(r['p99'])} at step {r['step']}, a valid run.", [r["run_id"]]))
    elif top_valid is None:
        gates.append(_gate("platform_overhead", cfg, "INCONCLUSIVE", "No valid run.", []))
    elif beyond:
        worst = max(beyond, key=lambda r: r["p99"] or 0)
        gates.append(_gate("platform_overhead", cfg, "INCONCLUSIVE",
                           f"Held through step {top_valid['step']} (p99 {_ms(top_valid['p99'])}). "
                           f"Above that, only invalid runs: worst p99 {_ms(worst['p99'])}, which can't count as a FAIL either.",
                           [top_valid["run_id"], worst["run_id"]]))
    else:
        gates.append(_gate("platform_overhead", cfg, "PASS",
                           f"Held through step {top_valid['step']}: p50 {_ms(top_valid['p50'])}, p99 {_ms(top_valid['p99'])}.",
                           [top_valid["run_id"]]))

    # Error rate
    errs = [r for r in valid if r["error_pct"] is not None]
    if not errs:
        gates.append(_gate("error_rate", cfg, "INCONCLUSIVE", "No valid run reports an error rate.", []))
    else:
        worst = max(errs, key=lambda r: r["error_pct"])
        status = "FAIL" if worst["error_pct"] >= t["error_rate_pct"] else "PASS"
        gates.append(_gate("error_rate", cfg, status,
                           f"Highest: {worst['error_pct']:.3f}% (step {worst['step']}) across {len(errs)} valid runs.",
                           [worst["run_id"]]))

    # Run validity
    if invalid:
        gates.append(_gate("run_validity", cfg, "FAIL",
                           " ".join(f"Step {r['step']} ({r['short_id']}): {'; '.join(r['invalid_because']) or 'invalid'}."
                                    for r in invalid), [r["run_id"] for r in invalid]))
    else:
        gates.append(_gate("run_validity", cfg, "PASS" if used else "INCONCLUSIVE",
                           f"All {len(used)} runs valid.", [r["run_id"] for r in used]))
    return gates


def order_gates(computed: List[Dict[str, Any]], static: List[Dict[str, Any]], order: Optional[List[str]]) -> List[Dict[str, Any]]:
    gates = computed + [dict(g, runs=[]) for g in static]
    if not order:
        return gates
    rank = {gid: i for i, gid in enumerate(order)}
    return sorted(gates, key=lambda g: rank.get(g["id"], len(rank)))


def explain_verdict(gates: List[Dict[str, Any]]) -> Dict[str, Any]:
    """Spec §7, in order. Returns the verdict, the rule that decided it, and the gates that triggered it."""
    rule1 = [g["id"] for g in gates if g["kind"] == "correctness" and g["status"] == "FAIL"]
    if rule1:
        return {"verdict": "FAIL", "rule": 1, "triggers": rule1}
    rule2 = [g["id"] for g in gates
             if g["status"] in ("INCONCLUSIVE", "NOT_APPLICABLE") or (g["kind"] == "validity" and g["status"] == "FAIL")]
    if rule2:
        return {"verdict": "INCONCLUSIVE", "rule": 2, "triggers": rule2}
    rule3 = [g["id"] for g in gates if g["status"] == "FAIL"]
    if rule3:
        return {"verdict": "FAIL", "rule": 3, "triggers": rule3}
    return {"verdict": "PASS", "rule": 4, "triggers": []}


# --- page ------------------------------------------------------------------

def build(cfg: Dict[str, Any], runs_dir: Path, head: str, today: str) -> Dict[str, Any]:
    runs = load_runs(cfg, runs_dir)
    gates = order_gates(compute_gates(runs, cfg), cfg.get("static_gates", []), cfg.get("gate_order"))
    return {
        "as_of": today,
        "tree": head,
        "label": "PREVIEW",
        "thresholds": cfg["thresholds"],
        "headline": headline(runs, cfg),
        "results": results(runs, cfg),
        "gates": gates,
        "verdict": explain_verdict(gates),
        "runs": runs,
        "d56": cfg.get("d56", []),
        "not_established": cfg.get("not_established", []),
        "reproduce": cfg.get("reproduce", []),
    }


def render(template: str, data: Dict[str, Any]) -> str:
    if SLOT not in template:
        raise ValueError(f"template has no {SLOT} slot")
    payload = json.dumps(data, ensure_ascii=False, indent=1).replace("</", "<\\/")
    return template.replace(SLOT, payload)


def git_head() -> str:
    return subprocess.run(["git", "rev-parse", "--short=7", "HEAD"], cwd=ROOT,
                          capture_output=True, text=True, check=True).stdout.strip()


def main(argv: Optional[List[str]] = None) -> int:
    p = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    p.add_argument("--config", type=Path, default=ROOT / "benchmarks/report-runs.json")
    p.add_argument("--runs", type=Path, default=ROOT / "benchmarks/runs")
    p.add_argument("--template", type=Path, default=ROOT / "benchmarks/bench_page/template.html")
    p.add_argument("--out", type=Path, default=ROOT / "benchmarks/site/index.html")
    args = p.parse_args(argv)

    cfg = json.loads(args.config.read_text())
    data = build(cfg, args.runs, git_head(), date.today().isoformat())
    args.out.parent.mkdir(parents=True, exist_ok=True)
    args.out.write_text(render(args.template.read_text(), data))

    missing = [r["run_id"] for r in data["runs"] if r["status"] == "missing"]
    h = data["headline"]
    print(f"wrote {args.out}")
    print(f"headline: {h['completed_whole']} completed payments/s, step {h['step']}, {h['run_id']}" if h else "headline: none (no valid passing run)")
    print(f"verdict: {data['verdict']['verdict']} (rule {data['verdict']['rule']})")
    if missing:
        print("missing result.md (shown as missing evidence): " + ", ".join(missing), file=sys.stderr)
    return 0


if __name__ == "__main__":
    sys.exit(main())
