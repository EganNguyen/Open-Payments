# Keeping the Results Page in Sync

How to update the [OP-BENCH Results](https://egannguyen.github.io/Open-Payments/) page when evidence changes, without typing a number into it.

Status: Interim, until `op-bench` writes `suite.html` ([plan P7b](../superpowers/plans/2026-11-10-op-bench-html-report.md)) · Last updated: 2026-11-10

## Where each fact lives

The page is generated. Every fact on it has one home, and you change it there.

| On the page | Home | Changed by |
|---|---|---|
| Every run number: rates, latency, CPU, checks, validity | `benchmarks/runs/<run-id>/result.md` | `benchmarks/run.sh`, never by hand |
| Which runs the page uses, and why one is excluded | [`benchmarks/report-runs.json`](../../benchmarks/report-runs.json) `runs` | You |
| Headline, run-based gates, overall verdict | Computed by [`bench_page.py`](../../benchmarks/bench_page.py) | Nobody; they follow from the runs |
| Gates with no run data yet (TX1, C54, soak…) | `report-runs.json` `static_gates` | You, only once their evidence exists |
| D56 checks | [Phase 1 exit record](../phase-gates/phase-1.md#d56-laptop-verification), mirrored in `report-runs.json` `d56` | You, both in one commit |
| The tracked record of a run | [BASELINE](../../benchmarks/BASELINE.md) | You, before the page |

`benchmarks/runs/` is git-ignored, so a run directory exists only on the machine that ran it. BASELINE is the tracked copy. Update BASELINE first, then the page.

## Rules

1. **No number is typed into the page, the template, the README or `report-runs.json`.** If a number is wrong, the run's `result.md` is wrong or the run is the wrong one.
2. **Never delete a run from `report-runs.json`.** Exclude it with a reason. The ledger shows excluded runs on purpose.
3. **A static gate changes status only with its evidence.** Put the commit, test command or run ID in its `evidence` text, as the Stage 1 progress log does.
4. **Rates are whole payments and requests per second, rounded down.** `bench_page.py` does this; the gates still use the recorded value.
5. **A missing `result.md` stays missing.** The page shows it as missing evidence. Don't copy numbers from BASELINE into the page to fill it.

## After a run finishes

1. Check the run is real evidence: `result.md` says `Run valid: yes` or records why not, and the tree was clean (`config.json` has `"git_dirty": false`).
2. Add the run to BASELINE: its row, run ID and anything it changes in the text.
3. Add it to `report-runs.json`:

   ```json
   { "id": "20261111T090000Z-abc1234-s6-1f2e" }
   ```

   Use `"note"` for context the ledger should show, such as `"Two API instances"`. If it shouldn't count, add it with `"exclude": "why"` instead.
4. Build and check it locally:

   ```bash
   scripts/bench-page.sh
   ```

   The script runs the builder's tests, writes `benchmarks/site/index.html`, refreshes the README thumbnails, and prints the headline and the verdict. Open the page and check the items under [Before you publish](#before-you-publish).
5. Commit the run list, BASELINE and the thumbnails together:

   ```bash
   git add benchmarks/report-runs.json benchmarks/BASELINE.md docs/design/op-bench-report/preview-light.png docs/design/op-bench-report/preview-dark.png
   ```

   ```bash
   git commit -m "docs: add step 6 run 1f2e to the results page"
   ```

6. Publish, then push `main`:

   ```bash
   scripts/bench-page.sh --publish
   ```

   ```bash
   git push origin main
   ```

   Publish after the commit, so the page's `tree` names a commit that holds its run list. `--publish` rebuilds the thumbnails with that commit; if they changed, commit them in a second, small commit.

## When other evidence changes

- **A static gate gets evidence,** for example TX1 is built and passes. Change its `status` and `evidence` in `static_gates` in the same commit that records the evidence in [Stage 1 progress](../gates/stage-1-progress.md). When a gate gets run data, move it from `static_gates` into `bench_page.py` with a test, rather than keeping a hand-set status.
- **A D56 check runs.** Update its row in the [Phase 1 exit record](../phase-gates/phase-1.md#d56-laptop-verification) and its `result` in `report-runs.json` `d56` in one commit. Results are `NOT RUN`, `BLOCKED`, `PASS`, `FAIL` or `INCONCLUSIVE`.
- **A threshold or milestone changes** through a decision in [Decisions](../decisions.md). Change `thresholds` or `milestone_*` in `report-runs.json` in the same commit as the decision. The page reads them from there.
- **A run turns out to be bad,** for example an E8-style harness defect. Add `"exclude"` with the reason and the escalation ID. Don't remove it.

## Before you publish

Open `benchmarks/site/index.html` and check:

- The headline the script printed is the run you expect.
- The verdict and the rule that stopped it match what the gates show.
- Every run you added appears in the ledger as `IN REPORT` or `EXCLUDED`, and none as `MISSING` unless you expected it.
- The README thumbnails (`git diff --stat docs/design/op-bench-report`) changed only if the first screen changed.

## Troubleshooting

| Symptom | Cause | Fix |
|---|---|---|
| `missing result.md` printed for a run | The run directory isn't on this machine | Build on the machine that ran it, or leave it as missing evidence |
| `headline: none` | No listed run is both valid and passing its gates | Correct, if true. The page says so instead of showing a number |
| Builder tests fail | `bench_page.py` or its parsing broke | Fix the code; don't publish |
| A number is `not measured` | That line is missing from `result.md` | Check the run's `report.py` version; never fill it by hand |
| `Chrome not found` | No Chrome at the default path | `CHROME=/path/to/chrome scripts/bench-page.sh` |
| `--publish` push rejected | `gh-pages` moved on GitHub | Rerun; the script fast-forwards `gh-pages` before it commits |
| Live page unchanged | GitHub Pages is still building | `gh api repos/EganNguyen/Open-Payments/pages/builds/latest --jq .status` until `built` |

## What this page doesn't do

- It doesn't run benchmarks or read Prometheus. It reads `result.md` files.
- It doesn't replace BASELINE, the Phase 1 exit record or `op-bench verify`.
- Its verdict follows spec §7, but it isn't the suite's verdict. That arrives with `op-bench`.
