# Documentation Restructure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restructure the seven Open Payments docs into the approved 10-file set, apply decisions D1-D13 and the listed corrections, and lose no IDs or checklist rows.

**Architecture:** A throwaway checker captures a baseline of the old docs, then enforces the spec's acceptance criteria. New docs are written in dependency order against a fixed heading contract, so links can be written before their targets exist. Old files are removed last.

**Tech Stack:** Markdown, Mermaid, git, Python 3 standard library (checker only).

**Spec:** `docs/superpowers/specs/2026-11-05-docs-restructure-design.md`. Read it first: its section 4 is the content mapping, section 5 the decisions, section 6 the corrections and section 7 the removals.

## Global Constraints

- Branch: `docs/restructure`, created from `main`. Never commit to `main`.
- Checker: `/private/tmp/claude-502/-Users-tim2k-Open-Payments/27389562-82ed-4beb-8bdd-08081b5f1be4/scratchpad/check_docs.py` (call it `$CHECK`). It is never added to the repo.
- Every commit message ends with: `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`
- Header of every doc: `# Title`, a one-sentence purpose, then `Status: Draft · Last updated: 2026-11-05`.
- Status words: `Committed`, `Proposed`, `Open`. No "(extension)".
- Links are relative with heading anchors. No `§`, and no "section N" pointing at another doc.
- Headings are unnumbered and match the contract below exactly.
- Terms: **completed payments/s** for the goal, **req/s** for HTTP load, **Payments API**, **outbox publisher**, **PSP**.
- Code examples are C# or SQL. k6 stays JavaScript.
- All numbers remain targets or starting values until measured.
- Move existing files with `git mv` so history follows them.

## Heading contract

Level-2 headings per doc. Link only to these or to their own subheadings.

| Doc | `##` headings, in order |
|---|---|
| `docs/architecture.md` | Goals and SLOs · Principles · Components and boundaries · Cells and routing · Payment flow · Crash recovery · Data model · Ledger · PSP integration · Events and webhooks · Scope and status |
| `docs/payment-lifecycle.md` | Overview · States · Transitions · Transition rules · Refund rules · The Unknown state · Capture modes · Timers · Worked examples · Merchant events · Proposed rules · Open questions |
| `docs/engineering-standards.md` | Load control · .NET runtime · Libraries · Database · Kubernetes and operations · Security and fraud · Observability |
| `docs/verification.md` | How verification works · Correctness checks · Load tests · Chaos and failure tests · Unit and property tests · Static gates · Operational drills · Production problem checklist · Production readiness checklist · Progress · Build phases |
| `docs/decisions.md` | Decision records (each as `### D1` ... `### D14`) · Assumptions and limits · Open questions |
| `docs/glossary.md` | Terms · Standard names |
| `docs/scaling-playbook.md` | The process · Measures and gates · Ramp plan · Workload and load generation · Capacity sizing · Bottleneck runbook · Decision gates · Results log · Final report |
| `docs/failure-scenarios.md` | Rules for every failure · Failure catalogue · Edge cases · Runbooks · Drills · Open questions |
| `docs/deadlock-handling.md` | Where deadlocks can happen here · How a deadlock forms · Prevention · Handling a deadlock · Proposed automation · Observability · Testing |

`### D14` records the existing cells decision: build one cell first and split only if gate G1 requires it. `### D11` also holds the .NET rationale, trade-offs and revisit triggers.

## Review Focus

Failure modes the ID checks would miss. Each is pinned by a checker rule in Task 1.

1. **Stale values survive a decision.** A reader finds the old rule in a doc that wasn't the decision's owner, such as "3x" or ".NET 8". Pinned by `STALE`.
2. **A move silently drops an exact value.** A timeout or setting vanishes while every ID still exists. Pinned by `REQUIRED_VALUES`.
3. **A decision is recorded but not applied.** `decisions.md` says one thing and the owning doc another. Pinned by `DECISION_APPLIED`.
4. **Anchors look right but don't resolve,** especially `.NET runtime`, which GitHub slugs as `net-runtime`. Pinned by the GitHub slug rule.
5. **A proposal reads as committed.** Pinned by the status rule on the state and transition tables.

---

### Task 1: Baseline commit and acceptance checker

**Files:**
- Commit: the current uncommitted docs, spec and plan
- Create (outside the repo): `$CHECK` and `baseline.json` beside it

**Interfaces:**
- Produces: `python3 $CHECK baseline`, `python3 $CHECK doc <path>`, `python3 $CHECK all`. Each exits non-zero and prints every failure.

- [ ] **Step 1: Create the branch and commit the baseline**

```bash
git switch -c docs/restructure
git add README.md failure-scenarios.md payment-system-design.md scaling-playbook.md deadlock-handling.md payment-lifecycle.md
git commit -m "docs: add lifecycle and deadlock guides, sync cross-references"
git add docs/superpowers
git commit -m "docs: add restructure spec and implementation plan"
```

Add the trailer to both messages.

- [ ] **Step 2: Implement `baseline`**

Read the old root docs and write `baseline.json` with: FS, E, R and G IDs; test IDs from design `## 18.`; `(number, problem)` pairs from `## 19.` and `(number, item)` pairs from `## 20.`. Assert the counts 42, 17, 7, 5, 66, 108 and 25.

- [ ] **Step 3: Implement `doc <path>`**

Skip `docs/superpowers/`. For the given doc, fail on:

- Header not matching the Global Constraints format.
- `##` headings differing from the contract (README excluded).
- `(extension)`, `§`, or a Python fence.
- `STALE` phrases, case-insensitive (skip `decisions.md`, which may quote old rules):
  `sized at 3x`, `.NET 8`, `only writer`, `one writer`, `durable queue`, `Dead letter queue`, `40P01", "40001", "55P03`, `deadlock_timeout | 200 ms`, `(extension)`, `Design / proposed`.
- Missing `REQUIRED_VALUES` for that doc:

| Doc | Exact strings |
|---|---|
| `architecture.md` | `hash(payment_id) % 64`, `FOR UPDATE SKIP LOCKED`, `min.insync.replicas=2`, `source_event_id`, `InquireAsync`, `(merchant_id, idempotency_key)` |
| `engineering-standards.md` | `QueueLimit = 0`, `64 * 1024`, `PooledConnectionLifetime`, `DOTNET_GCHeapHardLimitPercent`, `idle_in_transaction_session_timeout`, `AsNoTracking`, `CA2016`, `REINDEX CONCURRENTLY`, `terminationGracePeriodSeconds` |
| `payment-lifecycle.md` | `payments_unresolved`, `Idempotency-Key`, `payment.authorized`, `refund_pending_minor` |
| `scaling-playbook.md` | `ramping-arrival-rate`, `N = ceil( T / (C × U) )`, `L = λ × W` |
| `deadlock-handling.md` | `pg_blocking_pids`, `pg_stat_database.deadlocks`, `log_lock_waits` |
| `failure-scenarios.md` | `Retry-After`, `attempt_id`, `CK1-CK6` |

- `DECISION_APPLIED` strings for that doc:

| Doc | Exact strings (decision) |
|---|---|
| `architecture.md` | `one write path`, `payment-transition module` (D2); `overload survival` (D4); `operation` in the `payment_attempts` DDL (D5); `refund_pending_minor` (D6); `merchant_id` in the DDL of `payment_attempts`, `outbox`, `ledger_transactions` and `ledger_entries` (D8); `advisory` (D9); `one shared payment-path dependency` (D10); `.NET 10` (D11) |
| `engineering-standards.md` | `deadlock_timeout`, `100 ms` (D1); `ANY 1` (D7) |
| `scaling-playbook.md` | `completed payments/s`, `8,300` (D3) |
| `payment-lifecycle.md` | `REVIEW` in the state table and in a Mermaid transition (D12) |
| `verification.md` | `C34`, `C35` (D1, D9) |

- Status rule, for `payment-lifecycle.md`: every row in States and Transitions naming `VOIDED`, `EXPIRED`, `SETTLED`, `PAID_OUT`, `DISPUTED` or `CHARGED_BACK` contains `Proposed`.

- [ ] **Step 4: Implement `all`**

Run `doc` on every file, then the spec's section 9 criteria:

- Baseline IDs and row pairs exist in their home doc. Checklist rows are matched by number and problem name.
- Each of the 66 test IDs plus C34 and C35 has one definition row in `verification.md` and appears under `## Build phases`.
- Every cited test ID is defined.
- Every relative link and anchor resolves, using GitHub slugs: lowercase, drop punctuation other than hyphens and spaces, spaces become hyphens.
- Old filenames are gone, and `section N` is not followed by a reference to another doc.
- Normalized `sql` and `mermaid` fences are unique across docs.
- `### D1` to `### D14` exist in `decisions.md`.
- Line totals are printed against the spec's section 2.

- [ ] **Step 5: Confirm the starting state**

Run `python3 $CHECK baseline`. Expected: counts match and it exits 0.
Run `python3 $CHECK all`. Expected: FAIL, listing the missing new docs.

### Task 2: `decisions.md` and `glossary.md`

**Files:** Create `docs/decisions.md`, `docs/glossary.md`

**Interfaces:**
- Produces: anchors `decisions.md#d1` to `#d14`, and the `glossary.md` terms.

- [ ] **Step 1: Write `decisions.md`.** One short record per decision (Decision, Why, Consequence, Status `Committed`), taken from spec section 5. Add D14. Move in design §21, the dotnet doc's rationale, risks and revisit steps for D11, and the PSP-capability assumption and `Open` items listed in spec section 6.
- [ ] **Step 2: Write `glossary.md`.** Terms from README §4, plus the standard names rule.
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc docs/decisions.md` and the same for `docs/glossary.md`. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: add decision records and glossary`.

### Task 3: `architecture.md`

**Files:** Create `docs/architecture.md`

**Interfaces:**
- Consumes: `decisions.md#d1` to `#d14`.
- Produces: the DDL other docs link to, and the component table with the callback receiver as a named part of the Payments API.

- [ ] **Step 1: Write the doc** from the spec section 4 mapping. Merge README §5.3 and design §4 into one component table. Use README's boundary diagram, updated for D2 and D10.
- [ ] **Step 2: Apply D2, D4, D5, D6, D8, D9, D10 and D11, plus the outbox and missing-table corrections.** Add `refund_pending_minor` to `payments`, with `check (refunded_minor + refund_pending_minor <= captured_minor)`. Link to `payment-lifecycle.md#refund-rules` instead of repeating refund SQL.
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc docs/architecture.md`. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: add architecture doc`.

### Task 4: `payment-lifecycle.md`

**Files:** Move `payment-lifecycle.md` to `docs/payment-lifecycle.md` and edit

**Interfaces:**
- Consumes: `architecture.md#data-model`.
- Produces: `#refund-rules`, `#transitions`, `#merchant-events`.

- [ ] **Step 1: Restructure** to the contract. Apply the spec's lifecycle row from section 4: E and FS links replace the LC cases, and metrics and tests move out.
- [ ] **Step 2: Apply D6 and D12, plus corrections.** Write the three-step refund SQL (reserve, confirm, release). Fix the capture SQL so it sets `captured_minor` and filters on `merchant_id`. Mark partial capture and the extension rows `Proposed`. State that `CREATED` is never visible outside transaction A. Record refund-after-settlement and dispute-after-partial-refund as `Open`.
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc docs/payment-lifecycle.md`. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: restructure payment lifecycle`.

### Task 5: `engineering-standards.md`

**Files:** Create `docs/engineering-standards.md`

**Interfaces:**
- Consumes: `decisions.md#d1`, `#d7`, `#d11`.
- Produces: `#database`, `#load-control`, `#observability`.

- [ ] **Step 1: Write the doc** from design §12-§17 and the `HttpClient` details in §10, with the dotnet tool table under Libraries. Merge the lifecycle metrics into Observability.
- [ ] **Step 2: Apply D1, D7 and D11.** Add `deadlock_timeout` 100 ms with `lock_timeout` 200 ms. Retry only `40001` and `40P01`; `55P03` returns 503 with `Retry-After`. Use two standbys with quorum `ANY 1`.
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc docs/engineering-standards.md`. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: add engineering standards`.

### Task 6: `verification.md`

**Files:** Create `docs/verification.md`

**Interfaces:**
- Produces: the definitions of all test IDs, and `#build-phases`.

- [ ] **Step 1: Move** design §0, §18, §19, §20 and §22, the lifecycle testing section and the failure doc's status table. Replace each `§` pointer with a named link, keeping row numbers and problem names unchanged.
- [ ] **Step 2: Apply the corrections.** Add C34 and C35. Update C3, C6, L3, L5, L6 and C32 for D3, D4, D6 and D12. Put S5 and S6 in Phase 1, and C34 and C35 in Phase 2. Add C29 to row 50. Make this list canonical and note the repeated names (rows 3/26, 29/43, 30/44).
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc docs/verification.md`. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: add verification catalogue and build phases`.

### Task 7: Scaling playbook and failure scenarios

**Files:** Move `scaling-playbook.md` and `failure-scenarios.md` to `docs/` and edit

**Interfaces:**
- Consumes: `verification.md`, `engineering-standards.md#load-control`, `decisions.md#d3`, `#d4`.

- [ ] **Step 1: Restructure the playbook.** Apply D3: steps 9 and 10 and the final report are gated on 5,000 completed payments/s, with the derivation of about 8,300 req/s from the example mix. Link instead of redefining CK6 and latency. Keep every gate and the results log. Make the k6 sketch's failed-request threshold note that 429 and 503 count as failures unless excluded.
- [ ] **Step 2: Restructure the failure doc.** Update FS-07 (D12), FS-12 (D4) and FS-42 (D1). Add C9, C16, C22, C34 and C35 to the drills, plus C33 marked "only if cells are adopted".
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc` on both docs. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: restructure scaling playbook and failure scenarios`.

### Task 8: `deadlock-handling.md`

**Files:** Move `deadlock-handling.md` to `docs/` and edit

**Interfaces:**
- Consumes: `engineering-standards.md#database`, `decisions.md#d1`, `#d13`.

- [ ] **Step 1: Cut to about 200 lines** per spec section 7. Keep the where-it-can-happen analysis, implicit-lock note, lock-ordering rule, watchdog query and retry rules.
- [ ] **Step 2: Apply D1, D13 and corrections.** Write one C# retry sketch with one backoff definition, retrying `40001` and `40P01` only, and returning 503. Mark all automation `Proposed`, remove queue mechanisms, and shed in the order set by `failure-scenarios.md`. Correct the Scenario 3 and foreign-key claims.
- [ ] **Step 3: Verify.** Run `python3 $CHECK doc docs/deadlock-handling.md`. Expected: exit 0.
- [ ] **Step 4: Commit** with message `docs: reduce deadlock guide to project-specific content`.

### Task 9: README, removal and final acceptance

**Files:** Modify `README.md`. Delete `payment-system-design.md`, `dotnet-for-payments.md`.

- [ ] **Step 1: Rewrite the README** to about 100 lines per spec section 4: overview, headline goals updated for D3 and D4, one walkthrough, status, combined doc map and reading order, limits. Describe the UX prototype as external to this repo.
- [ ] **Step 2: Confirm the content has moved.** Walk both old docs against the spec's section 4 mapping and section 7 removal list, section by section. Anything unaccounted for gets moved, or reported to the user.
- [ ] **Step 3: Delete the two old files** with `git rm`.
- [ ] **Step 4: Run the full check.** Run `python3 $CHECK all`. Expected: exit 0, with line totals printed.
- [ ] **Step 5: Validate Mermaid.** In the built-in browser, open a scratchpad HTML page that loads `mermaid@10.9.1` from jsDelivr and runs `mermaid.parse` on every Mermaid block. Expected: no parse errors. If the browser or CDN is unavailable, report this criterion as unverified rather than passed.
- [ ] **Step 6: Commit** with message `docs: slim README and remove superseded docs`.
- [ ] **Step 7: Report** the checker output, Mermaid result, line totals and anything the walk in Step 2 flagged. Do not push or open a PR unless asked.
