# OP-BENCH HTML Report Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.
>
> **For whoever executes this:** Follow [Delivery workflow](../../delivery-workflow.md#build-gate). Build-gate work (DeepSeek), reviewed afterwards by Opus in a fresh session. Stop and escalate in `docs/gates/stage-1-escalations.md` on any conflict with the design; never decide it yourself.

**Goal:** `op-bench report <suite-id>` also writes `suite.html`, an evidence-first page rendered from the same `SuiteReport` as `suite.md`, so every suite run produces the report designed in [the prototype](../../design/op-bench-report/prototype.html) with no number written by hand.

**Architecture:** A pure C# `HtmlWriter` beside P7's `MarkdownWriter` renders a `SuiteReport` to one self-contained HTML file: CSS from an embedded resource, the latency chart as server-rendered SVG, no script needed to read it, no external request. The verdict ladder comes from a new `VerdictEvaluator.Explain`, so the page shows the rule the evaluator actually applied rather than re-deriving it. `verify` never reads the HTML; it stays a rendering of `suite.json`.

**Tech Stack:** .NET 10, `System.Text.Encodings.Web.HtmlEncoder`, xUnit, Microsoft.Playwright (already pinned in `Directory.Packages.props`, used by `OpenPayments.Console.Browser.Tests`).

**Spec:** [OP-BENCH spec](../specs/2026-11-09-benchmark-suite-design.md) §7 (gates and verdict) and §8 (report contract). Visual spec: [`docs/design/op-bench-report/prototype.html`](../../design/op-bench-report/prototype.html), built from the Stage 1 evidence on 2026-11-10. Parent plan: [OP-BENCH build plan](2026-11-09-benchmark-suite.md), whose P1 and P7 types this plan consumes.

**Approval:** gate item **W6** in [OP-BENCH design gate](../../gates/op-bench-design.md#decided-without-the-owner). It adds `suite.html`, a `gates` array and the `runs[]` shape to the §8 contract. The owner confirmed W6 in chat on 2026-11-10.

**Order:** this is **P7b**. It starts after P7 is committed and before P8, so the first full suite run produces the page. Log tasks in `docs/gates/stage-1-progress.md` as `17.P7b.n`.

## Global Constraints

- Every constraint in the [parent plan](2026-11-09-benchmark-suite.md#global-constraints) applies, including the do-not-edit list and the commit author.
- `null` renders as the text `not measured`, never as `0`, `-`, an empty cell or a pass. `0` renders as `0`.
- `NOT_APPLICABLE` renders with its own chip and is never styled or counted as a pass.
- No hand-written result text. Every sentence that states a result is built from a `SuiteReport` field. Fixed copy is limited to labels, headings, the §7 rule texts and the "What this does not establish" list from P7.
- The file makes no network request: no `http://` or `https://` in any `src`, `href` or CSS `url()`. Font stack `"IBM Plex Sans", system-ui, sans-serif` and `"IBM Plex Mono", ui-monospace, monospace`; no web font is loaded.
- Every interpolated string goes through `HtmlEncoder.Default.Encode`. Evidence paths render as relative links inside the suite directory.
- The page reads without JavaScript. Copy buttons and the ledger filter are optional enhancements in one inline `<script>`; without it, all rows show.
- Colours are CSS custom properties defined on `:root` with a `prefers-color-scheme: dark` override, copied from the prototype's tokens. No literal colour in component rules.
- Width: no horizontal page scroll at 400 px; only tables and the chart scroll inside their own container.
- `verify` must not read `suite.html`. `latest.html` is a copy, like `latest.md`.

## Review Focus

1. **A suite with no performance runs** (`smoke`, `correctness`, `reliability`): the chart and step table are replaced by one line, "No performance runs in this suite.", not empty axes. Test in Task 3.
2. **A value far off scale**, like step 6's p99 of 967,400.19 ms seen on 2026-11-09: the chart pins the point to the top edge with an "off scale" label; the table shows the real value. Test in Task 3.
3. **A gate ID the renderer has never seen** (a gate added after this plan): it renders as a normal row with its ID and requirement, not dropped. Test in Task 2.
4. **Hostile or long strings** in a reason, run ID or evidence path (`<script>`, 300 characters, no spaces): encoded, and wrapped without widening the page at 400 px. Tests in Tasks 2 and 4.
5. **A measured zero next to an unmeasured null** (deadlocks `0`, retries `null`): one shows `0`, the other `not measured`. Test in Task 2.

---

### Task 1: Report data the page needs

**Files:**
- Modify: `tools/OpenPayments.Bench/Gates/VerdictEvaluator.cs`
- Modify: `tools/OpenPayments.Bench/Report/SuiteReport.cs`
- Test: `tests/OpenPayments.Bench.Tests/VerdictExplanationTests.cs`, `tests/OpenPayments.Bench.Tests/SuiteReportTests.cs`

**Interfaces:**
- Consumes: `GateResult`, `GateStatus`, `GateKind`, `Verdict` (P1); `SuiteReport` and its JSON writer (P7).
- Produces:

```csharp
namespace OpenPayments.Bench.Gates;

// Rule is 1-4 in spec §7 order. TriggeringGateIds lists the gates that made that rule fire
// (empty for rule 4). For rule 2 it also includes required gate IDs that are missing.
public sealed record VerdictExplanation(Verdict Verdict, int Rule, IReadOnlyList<string> TriggeringGateIds);

public static class VerdictEvaluator
{
    public static VerdictExplanation Explain(IReadOnlyList<GateResult> gates, IReadOnlySet<string> requiredGateIds);
    // Evaluate(...) becomes Explain(...).Verdict. Its signature and tests are unchanged.
}
```

```csharp
namespace OpenPayments.Bench.Report;

// One entry of performance.completed_payments_per_second.runs[] in suite.json (snake_case on the wire).
public sealed record RunSummary(
    string RunId, string Step, int TargetRequestsPerSecond,
    double? AdmittedPerSecond, double? CompletedPerSecond,
    double? PlatformP50Ms, double? PlatformP95Ms, double? PlatformP99Ms,
    double? ApiCpuPercent, bool Valid, IReadOnlyList<string> InvalidReasons,
    bool CountsTowardGates, string ResultPath);   // ResultPath relative to the suite directory

// SuiteReport gains:
//   IReadOnlyList<GateResult> Gates          -> "gates": [{ "id", "kind", "status", "reason", "evidence" }]
//   IReadOnlyList<RunSummary> Runs           -> performance.completed_payments_per_second.runs
//   string GateRequirement(string gateId)    -> text from thresholds.json, "" if unknown
//   double PlatformP50GateMs, PlatformP99GateMs -> from thresholds.json (25 and 100 today)
```

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Explain_reports_rule_1_with_the_failed_correctness_gate()
// gates: Correctness Fail, Throughput Inconclusive -> Verdict.Fail, Rule 1, ["correctness"]

[Fact] public void Explain_reports_rule_2_with_every_inconclusive_and_missing_gate()
// gates: Throughput Inconclusive, CrashRecovery NotApplicable; required also has "soak" absent
// -> Verdict.Inconclusive, Rule 2, ["throughput", "crash_recovery", "soak"] in that order (input order, then missing in required-set order sorted ordinal)

[Fact] public void Explain_reports_rule_3_for_a_performance_fail()
[Fact] public void Explain_reports_rule_4_with_no_triggers_when_all_pass()
[Fact] public void Explain_and_Evaluate_agree_on_every_existing_verdict_test_case()

[Fact] public void Suite_json_round_trips_gates_and_runs_with_nulls_kept()
// a RunSummary with PlatformP95Ms = null and ApiCpuPercent = 0 serialises to "platform_p95_ms": null and "api_cpu_percent": 0,
// and reads back equal
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Bench.Tests --filter "FullyQualifiedName~VerdictExplanation|FullyQualifiedName~SuiteReportTests"`
Expected: FAIL to compile, `Explain` and `RunSummary` not defined.

- [ ] **Step 3: Implement `Explain` and the two `SuiteReport` fields**

`Evaluate` delegates to `Explain`, so there is one implementation of the §7 order. `StepScenario` and `TargetScenario` (P1) fill `RunSummary` from each run directory's `config.json`, `k6-summary.json`, `resources.json` and the validity lines of `result.md`; a field the run didn't record stays `null`.

- [ ] **Step 4: Run the tests, then the whole suite**

Run: `dotnet test tests/OpenPayments.Bench.Tests` then `dotnet build OpenPayments.slnx -warnaserror`
Expected: PASS; P7's `Report_round_trips_through_verify` still passes.

- [ ] **Step 5: Commit**

```bash
git add tools/OpenPayments.Bench/Gates/VerdictEvaluator.cs tools/OpenPayments.Bench/Report/SuiteReport.cs tests/OpenPayments.Bench.Tests/VerdictExplanationTests.cs tests/OpenPayments.Bench.Tests/SuiteReportTests.cs
git commit -m "feat: explain which verdict rule applied and record run summaries"
```

### Task 2: `HtmlWriter`, every section except the chart

**Files:**
- Create: `tools/OpenPayments.Bench/Report/HtmlWriter.cs`
- Create: `tools/OpenPayments.Bench/Report/suite.css` (embedded resource; `<EmbeddedResource Include="Report/suite.css" />` in the csproj)
- Test: `tests/OpenPayments.Bench.Tests/HtmlWriterTests.cs`

**Interfaces:**
- Consumes: `SuiteReport` (with `Gates`, `Runs`, `GateRequirement`), `VerdictEvaluator.Explain`, `RunManifest` (P1).
- Produces:

```csharp
namespace OpenPayments.Bench.Report;

public static class HtmlWriter
{
    public static string Render(SuiteReport report, VerdictExplanation explanation);
    // Task 3 adds: internal static string RenderLatencyChart(IReadOnlyList<RunSummary> runs, double p50GateMs, double p99GateMs);
}
```

Sections, in order, matching the prototype (each a `<section id="…">`): `verdict`, `claims`, `gates`, `ramp` (Task 3 fills it; this task writes the step table only), `runs`, `repro`. The D56 table in the prototype is **not** part of this page: it isn't suite data.

- **Verdict:** the overall verdict word, the label chip (`PREVIEW` / `RECORDED`), the suite ID, commit and date. The ladder lists the four §7 rules in order; rule `explanation.Rule` is marked `stopped here` with its triggering gate IDs; earlier rules `not triggered`; later rules `not reached`.
- **Claims:** "Established" lists each `Pass` gate as its requirement plus its measured value and source. "Not established" lists P7's "What this does not establish" items, then each non-`Pass` gate's `Reason`.
- **Gates:** one row per `report.Gates` entry in input order: name (the ID if unknown), requirement, status chip, reason, evidence links.
- **Run ledger:** one row per `RunSummary`, chip `IN REPORT` when `CountsTowardGates` else `EXCLUDED`, reasons joined with "; ".
- **Reproduce:** a key/value list from `RunManifest`, and the `op-bench run <suite>` command.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Null_value_renders_as_not_measured()        // RunSummary.PlatformP95Ms = null -> cell text "not measured"
[Fact] public void Zero_renders_as_zero_next_to_null()         // deadlocks 0, retries null -> "0" and "not measured" (Review Focus 5)
[Fact] public void Not_applicable_has_its_own_chip()           // class "st na", text "NOT_APPLICABLE", not "st pass"
[Fact] public void Ladder_stops_at_the_explained_rule()        // Rule 2 -> li 2 has class "hit" and "stopped here"; li 1 "not triggered"; li 3,4 "not reached"
[Fact] public void Unknown_gate_id_renders_as_a_row()          // gate "future_gate", requirement "" -> row present with "future_gate" (Review Focus 3)
[Fact] public void Hostile_strings_are_encoded()               // reason "<script>alert(1)</script>" -> output contains "&lt;script&gt;", not "<script>alert" (Review Focus 4)
[Fact] public void Page_makes_no_external_request()            // regex (src|href)="https?:// and url\(https?: -> no match
[Fact] public void Established_lists_only_pass_gates()         // Pass "error_rate", Inconclusive "throughput" -> "throughput" absent from #claims established list
[Fact] public void Html_and_markdown_agree_on_verdict_and_statuses() // same report -> same verdict word and same status per gate ID in both writers
```

Build reports in a `ReportFixtures` helper in the test project; one fixture, `Stage1Preview()`, uses the 2026-11-09 values from `benchmarks/BASELINE.md` so the rendered page can be compared with the prototype by eye.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Bench.Tests --filter FullyQualifiedName~HtmlWriterTests`
Expected: FAIL to compile, `HtmlWriter` not defined.

- [ ] **Step 3: Implement `HtmlWriter.Render`**

Use a `StringBuilder` and one private `E(string?)` encoding helper; `E(null)` is never called for values, because a `null` value goes through `Value(double?, string unit)` which returns the `not measured` span. Copy `suite.css` from the prototype's `<style>` with the Google Fonts link removed and the font tokens set to the stacks in Global Constraints. The inline `<script>` holds only the copy buttons and the ledger filter from the prototype, without `localStorage`.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/OpenPayments.Bench.Tests --filter FullyQualifiedName~HtmlWriterTests`
Expected: PASS, 9 tests.

- [ ] **Step 5: Commit**

```bash
git add tools/OpenPayments.Bench/Report/HtmlWriter.cs tools/OpenPayments.Bench/Report/suite.css tools/OpenPayments.Bench/OpenPayments.Bench.csproj tests/OpenPayments.Bench.Tests/HtmlWriterTests.cs tests/OpenPayments.Bench.Tests/ReportFixtures.cs
git commit -m "feat: render the suite report as an evidence-first HTML page"
```

### Task 3: Latency chart

**Files:**
- Create: `tools/OpenPayments.Bench/Report/LogScale.cs`
- Modify: `tools/OpenPayments.Bench/Report/HtmlWriter.cs`
- Test: `tests/OpenPayments.Bench.Tests/LatencyChartTests.cs`

**Interfaces:**
- Consumes: `RunSummary` (Task 1), `HtmlWriter` (Task 2).
- Produces:

```csharp
namespace OpenPayments.Bench.Report;

public sealed record LogScale(double DomainMin, double DomainMax, double RangeStart, double RangeEnd)
{
    public double Map(double value);           // log10 interpolation; value outside the domain is clamped
    public bool IsOffScale(double value);      // value < DomainMin or > DomainMax
}
```

Chart values, fixed: viewBox `0 0 720 312`, plot `x 64..690`, `y 270..24`. X is offered req/s, log domain `10^-0.15 .. 10^3.15`, ticks `1, 10, 100, 500, 1,000`. Y is platform overhead in ms, log domain `4 .. 250`, ticks `5, 10, 25, 50, 100, 200`. Dashed gate lines at 25 ms (p50) and 100 ms (p99), values read from `thresholds.json`, not literals. p99 line solid between valid runs, dashed into an invalid run. Valid runs are filled circles; invalid runs hollow. Runs sharing a target are offset ±10 px in X. Each point has a `<title>` with step, metric, value, validity and run ID. `aria-label` on the `<svg>` states the p99 range in words.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Map_places_gate_values_where_the_prototype_does()
// y scale: Map(100) ≈ 78.5 and Map(25) ≈ 161.0, within 0.2

[Fact] public void Invalid_run_is_drawn_hollow()               // circle fill "var(--surface)" for Valid = false
[Fact] public void Off_scale_value_is_pinned_and_labelled()    // p99 967400.19 -> cy == 24 and text "off scale"; step table still shows "967,400.19 ms" (Review Focus 2)
[Fact] public void No_performance_runs_replaces_the_chart()    // Runs empty -> no <svg class="chart">, text "No performance runs in this suite." (Review Focus 1)
[Fact] public void Null_latency_draws_no_point()               // PlatformP99Ms null -> no p99 circle for that run, run still in table
[Fact] public void Gate_lines_come_from_thresholds()           // RenderLatencyChart(runs, 25, 120) -> p99 gate line y == Map(120)
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Bench.Tests --filter FullyQualifiedName~LatencyChartTests`
Expected: FAIL to compile, `LogScale` not defined.

- [ ] **Step 3: Implement `LogScale` and `HtmlWriter.RenderLatencyChart`, called from the `ramp` section**

Format SVG coordinates with `CultureInfo.InvariantCulture` and one decimal.

- [ ] **Step 4: Run the tests**

Run: `dotnet test tests/OpenPayments.Bench.Tests`
Expected: PASS, including Task 2's tests.

- [ ] **Step 5: Commit**

```bash
git add tools/OpenPayments.Bench/Report/LogScale.cs tools/OpenPayments.Bench/Report/HtmlWriter.cs tests/OpenPayments.Bench.Tests/LatencyChartTests.cs
git commit -m "feat: draw platform overhead against offered load in the HTML report"
```

### Task 4: Write it from `op-bench report`, and check it in a browser

**Files:**
- Modify: `tools/OpenPayments.Bench/Program.cs` (the `report` command)
- Modify: `benchmarks/README.md`, `benchmarks/reports/README.md`
- Modify: `tests/OpenPayments.Bench.Tests/OpenPayments.Bench.Tests.csproj` (add `Microsoft.Playwright`)
- Test: `tests/OpenPayments.Bench.Tests/ReportCommandTests.cs`, `tests/OpenPayments.Bench.Tests/HtmlLayoutTests.cs`

**Interfaces:**
- Consumes: `HtmlWriter.Render` (Task 2), `VerdictEvaluator.Explain` (Task 1), P7's report command.
- Produces: `benchmarks/reports/<suite-id>/suite.html` and `benchmarks/reports/latest.html`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact] public void Report_writes_suite_html_and_latest_html()        // temp suite dir with a stored suite.json -> both files exist, latest.html == suite.html bytes
[Fact] public void Verify_passes_when_suite_html_is_deleted()        // delete suite.html -> op-bench verify exit 0
[Fact] public void Verify_ignores_a_tampered_suite_html()            // overwrite suite.html with "x" -> verify exit 0, verdict unchanged

[Trait("Category", "Browser")]
[Fact] public async Task Page_has_no_horizontal_scroll_at_400px_in_both_themes()
// Stage1Preview() plus a 300-character unbroken reason (Review Focus 4); viewport 400x900, ColorScheme Light then Dark:
// document.documentElement.scrollWidth <= 400, and body background-color differs between the two themes.
// When OP_BENCH_KEEP_HTML=1, the test also writes the rendered HTML to bin/stage1-preview.html (used in Step 5)

[Trait("Category", "Browser")]
[Fact] public async Task Page_has_no_serious_axe_violations()        // Deque.AxeCore.Playwright, impact serious or critical -> 0
```

Browser setup follows `OpenPayments.Console.Browser.Tests`: same Playwright install step, page loaded with `page.SetContentAsync(html)`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Bench.Tests --filter "FullyQualifiedName~ReportCommandTests|FullyQualifiedName~HtmlLayoutTests"`
Expected: FAIL: `suite.html` not written.

- [ ] **Step 3: Write `suite.html` in the `report` command next to `suite.md`, and copy it to `latest.html`**

Document both files in the "What a suite writes" table of `benchmarks/README.md` and in `benchmarks/reports/README.md`, with one line: open `latest.html` in a browser; it needs no server and no network.

- [ ] **Step 4: Run the whole check set**

Run: `dotnet build OpenPayments.slnx -warnaserror`, `dotnet test OpenPayments.slnx`, `.venv/bin/python -m pytest benchmarks/test_report.py`
Expected: all PASS.

- [ ] **Step 5: Render the fixture and compare it with the prototype by eye**

Run: `dotnet test tests/OpenPayments.Bench.Tests --filter Page_has_no_horizontal_scroll` with `OP_BENCH_KEEP_HTML=1`, which leaves the rendered fixture at `tests/OpenPayments.Bench.Tests/bin/stage1-preview.html`. Open it beside `docs/design/op-bench-report/prototype.html`. Expected: same sections in the same order, same verdict, same gate statuses for the gates both contain. Record any visual difference in the progress line; don't fix the prototype.

- [ ] **Step 6: Commit**

```bash
git add tools/OpenPayments.Bench/Program.cs benchmarks/README.md benchmarks/reports/README.md tests/OpenPayments.Bench.Tests/OpenPayments.Bench.Tests.csproj tests/OpenPayments.Bench.Tests/ReportCommandTests.cs tests/OpenPayments.Bench.Tests/HtmlLayoutTests.cs
git commit -m "feat: write suite.html from op-bench report"
```

## Not in this plan

- The D56 laptop verification table. It isn't suite data; it stays in [Phase 1](../../phase-gates/phase-1.md#d56-laptop-verification).
- Runs outside the suite, such as other sessions' runs in `benchmarks/runs/`. The ledger lists the suite's own `run_ids` only.
- Publishing the page anywhere. P8 still copies `suite.md` into the tracked `benchmarks/OP-BENCH-<date>.md`; `suite.html` stays in the git-ignored reports directory.
