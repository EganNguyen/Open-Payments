# Documentation Restructure: Design Spec

Status: awaiting review · Date: 2026-11-05 · Chosen approach: Option B (one home for each fact)

## 1. Goal

Make the Open Payments docs concise, easy to understand and well structured, and remove the contradictions found in the review.

**Requested:** concise, clear, well-structured Markdown, with a design agreed before implementation.

**Assumed (correct these if wrong):**

- Readers are the project owner and future engineers.
- The project is still design-stage, so no code depends on these docs.
- Moving files into `docs/` is acceptable.

**Out of scope:** writing code, running benchmarks, designing settlement and payouts, and adding doc tooling.

## 2. Target structure

| File | Only home for | Target lines |
|---|---|---|
| `README.md` | What the project is, status, reading order, doc map | ~100 |
| `docs/architecture.md` | Goals and SLOs, principles, components, boundaries, payment flow, data model, ledger, PSP integration, events | ~450 |
| `docs/payment-lifecycle.md` | States, transitions, refund rules, merchant event list | ~220 |
| `docs/engineering-standards.md` | Load control, .NET, database, Kubernetes, security and fraud rules, observability | ~300 |
| `docs/verification.md` | Test IDs, 108-row and 25-row checklists, progress counts, build phases and exit tests | ~320 |
| `docs/scaling-playbook.md` | Ramp plan, load-generator rules, sizing, decision gates, results log | ~200 |
| `docs/failure-scenarios.md` | Failure catalogue, edge cases, runbooks, drills | ~190 |
| `docs/deadlock-handling.md` | Project-specific deadlock risks, prevention, retry behavior, proposed automation | ~200 |
| `docs/decisions.md` | Decision records, assumptions and limits, open questions | ~150 |
| `docs/glossary.md` | Terms and standard names | ~50 |

Target total: about 1,800 lines, down from 2,522. These are estimates. Accuracy and preserved content take priority over hitting them.

## 3. Writing rules

1. **One home per fact.** Other docs link to it. The README may repeat only the headline goals.
2. **Same header everywhere:** title, one-sentence purpose, `Status · Last updated`.
3. **One status vocabulary:** `Committed`, `Proposed`, `Open`. This replaces "(extension)", "proposal" and "Later".
4. **Stable IDs.** Existing FS, E, R, CK, L, C, U, F, S, O and G IDs and checklist numbers keep their meaning. New IDs are added at the end of a series.
5. **Named links, not section numbers.** Use relative links to headings, such as `[Ledger](architecture.md#ledger)`. Unnumbered headings make this safe to reorganize.
6. **Standard terms** (defined in the glossary):
   - **completed payments/s** for the goal.
   - **req/s** for HTTP load. "TPS" is used only when it means completed payments/s.
   - **Payments API**, **outbox publisher**, **PSP**.
7. **Code examples use C# or SQL.** k6 stays JavaScript.
8. **Keep a voice readers can trust:** say plainly what is unmeasured or undecided, once per doc rather than in every section.

## 4. Content mapping

### `payment-system-design.md` (removed; content moves)

| Section | Destination |
|---|---|
| §0 How to read | `verification.md` intro |
| §1-§5 Goals, principles, architecture, services, cells | `architecture.md` |
| §6 Lifecycle | `payment-lifecycle.md`; the duplicate diagram is removed |
| §7 Flow and crash analysis | `architecture.md` |
| §8 Data model | `architecture.md`; §8.3 refund SQL moves to `payment-lifecycle.md` |
| §9-§11 Ledger, PSP adapters, events | `architecture.md`; `HttpClient` details move to `engineering-standards.md` |
| §12-§17 Load control, runtime, database, platform, security, observability | `engineering-standards.md` |
| §18-§20 Tests and checklists | `verification.md` |
| §21 Assumptions and limits | `decisions.md` |
| §22 Build order | `verification.md`; the short version in §4 is removed |

### `README.md`

| Section | Destination |
|---|---|
| §1, §8, §10 Overview, status, limits | Stay, shortened; limits link to `decisions.md` |
| §2 Goals | Headline goals stay; the full table is `architecture.md` |
| §3 Payment walkthrough | Stays as the README's one diagram |
| §4 Key ideas | `glossary.md` |
| §5.1-§5.5 Architecture | `architecture.md`; §5.3 merges with design §4 into one component table |
| §5.6 Service list | Merged into that table, then removed |
| §5.7 Feature list | `architecture.md`, as a scope-and-status table |
| §6 Why it is hard | Reduced to two or three lines |
| §7, §9 Doc map, reading order | One combined table |

### Other docs

| Source | Change |
|---|---|
| `payment-lifecycle.md` | Moves to `docs/`. Metrics go to `engineering-standards.md`, and testing goes to `verification.md`. LC1, LC2, LC4, LC5, LC7 and LC8 are replaced by links to E and FS entries. LC3 maps to FS-32. LC6 stays as a `Proposed` dispute rule. Duplicate intro text is removed. |
| `failure-scenarios.md` | Moves to `docs/`. The verification-status table goes to `verification.md`. Rules and runbooks stay. |
| `scaling-playbook.md` | Moves to `docs/`. Duplicate definitions of CK6 and latency are replaced by links. |
| `deadlock-handling.md` | Moves to `docs/` and is cut to project-specific content (see section 7). |
| `dotnet-for-payments.md` | Removed as a separate doc. Rationale, trade-offs and revisit triggers become a decision record. The tool table goes to `engineering-standards.md`. |

## 5. Design decisions

Applying these changes the design. Each one becomes a record in `decisions.md`. Approving this spec accepts every recommendation below unless you change it. Anything you defer stays in the docs as `Open`, with its contradiction stated rather than hidden. All values are starting points until measured.

| ID | Issue | Recommended decision | Consequence |
|---|---|---|---|
| D1 | `lock_timeout` (200 ms) fires before PostgreSQL's default `deadlock_timeout` (1 s) | Set `deadlock_timeout` to 100 ms and keep `lock_timeout` at 200 ms. Retry only `40001` and `40P01`. `55P03` returns 503 with `Retry-After`. | Deadlocks become visible and retried. Add test **C34**: a capture, refund and reconciler race on one payment. |
| D2 | "One writer" conflicts with the reconciler and callbacks | Replace it with **one write path**: a shared payment-transition module is the only code that writes money state. The API, reconciler, timers and callback receiver all call it. The callback receiver is hosted in the Payments API. | The README's "workers only read" rule gains named exceptions. |
| D3 | The final gate is req/s but the goal is completed payments/s | Gate steps 9 and 10 and the final report on 5,000 **completed payments/s**. Offered req/s is derived from the workload mix. | With the example mix, the target needs about 8,300 req/s, so capacity estimates rise. |
| D4 | The 15k burst is described both as sized-for and as shed | Define 15k as an **overload survival test**: excess is shed, 5k completed/s holds, and nothing crashes. Remove "sized at 3x". | Lower cost, but 15k/s is no longer a served-capacity promise. |
| D5 | Authorize and capture can share a PSP idempotency key | Record each PSP operation as its own `payment_attempts` row with an `operation` column and its own key. | Small schema and adapter-signature change. |
| D6 | Capture and refund timeouts have no defined handling, and refund ordering is undefined | Payment state changes only after the PSP confirms. Unresolved operations are tracked on the attempt or refund row and resolved by the reconciler. Refunds reserve first through a new `refund_pending_minor`, with limit `refunded + pending + amount <= captured`, then call the PSP, then confirm or release. | Refund SQL and tests C3 and C6 change. A refund shows as pending until confirmed. |
| D7 | One synchronous standby conflicts with surviving a zone loss at RPO 0 | Use two synchronous-capable standbys in different zones, with quorum `ANY 1`. | One more database replica per cell, and possibly a slightly different commit latency. |
| D8 | `merchant_id` is missing from tables said to have it | Add it to `payment_attempts`, `outbox`, `ledger_transactions` and `ledger_entries`. Narrow the rule to "every tenant-owned table", which excludes provider-scoped `psp_events`. | More storage. Row-level security can cover the ledger. |
| D9 | Balance check and reservation can race | Take a per-account advisory transaction lock on refund and payout balance checks only, never on the create-payment path. Add test **C35**: concurrent refunds across different payments against a low balance. | Refunds on one merchant account are serialized; cost measured in L1. |
| D10 | "Cells share nothing" conflicts with shared synchronous services | Run fraud decision and PSP adapters per cell. The vault stays shared to keep PCI scope small, and is documented as the one shared payment-path dependency. | Vault availability becomes a stated cross-cell risk. |
| D11 | .NET 8 reaches end of support on 10 Nov 2026 | Target .NET 10 (LTS), after confirming dates on Microsoft's policy page during implementation. | Stack line changes; library support must be rechecked before adoption. |
| D12 | "Held for review" has no state | Add `REVIEW` as a `Committed` non-final state, entered on an amount or currency mismatch and left through manual resolution. | Transition table, FS-07 and C32 are updated. Review tooling stays `Open`. |
| D13 | Deadlock proposals add queues the design forbids | Remove the critical tier's durable payment queue, the request dead-letter queue and limiter queueing. Overload is shed with 503. Remaining proposed tiers follow the shed order in `failure-scenarios.md`, so refunds are shed after webhooks are slowed. | Fewer proposed mechanisms, and principle "nothing confirmed before commit" stays intact. |

D12 and D13 were not numbered in the review. They come from its smaller-errors list and need your decision because they change behavior.

## 6. Corrections applied without a design decision

| Location | Correction |
|---|---|
| Lifecycle capture SQL | Set `captured_minor` and filter on `merchant_id` |
| Partial capture | Mark as `Proposed` wherever it appears |
| Lifecycle extensions | Mark every extension row in transition tables and the overview as `Proposed`. Record refund-after-settlement and dispute-after-partial-refund as `Open` |
| `CREATED` state | State that it is never visible outside transaction A |
| Scenario 3 (deadlock doc) | Remove the claim that a plain PostgreSQL `SELECT` takes a shared row lock |
| Deadlock doc §0 | Correct the claim that `ledger_transactions` has a foreign key to `payments`; list all automation layers as `Proposed` |
| Deadlock retries | One backoff definition; `503` only, removing "or 409" |
| Outbox DDL | Add `sequence` and `schema_version`, which the event rules already require |
| Missing tables | List `accounts`, inbox, currency, balance snapshot, delivery log and discrepancy tables as `Open` in the data model |
| Build phases | Add S5 and S6 to Phase 1, and C34 and C35 to Phase 2 |
| Drills | Add C9, C16 and C22; add C33 only if cells are adopted; add C34 and C35. C31 stays a CI suite |
| Checklist intro | Make `verification.md` the canonical problem list, because the source list isn't in the repo. Note that three problem names repeat (rows 3/26, 29/43, 30/44); numbering and the count of 108 stay |
| Checklist proofs | Add C29 as a proof for row 50 (replica lag) |
| Assumptions | Record that the design depends on the PSP supporting idempotency keys and status inquiry by our ID |
| README | Remove the contradictory "on the diagram" note, and describe the UX prototype as external to this repo |
| `dotnet-for-payments.md` | Hyperswitch, Formance and TigerBeetle are mentioned nowhere else: keep them as `Open` options in the .NET decision record |

## 7. Content deliberately removed

| Content | Reason |
|---|---|
| MySQL, SQL Server and Oracle deadlock notes | PostgreSQL only |
| `accounts.balance` timeline, wait-for graph, Scenarios 1-2 detail | The design has no balance updates on the payment path. A short general explanation and the lock-ordering rule stay |
| "True zero deadlocks" section and engine survey | Not actionable for this design. One line on implicit locks stays |
| Python `run_tx` examples | Replaced by one C# sketch |
| .NET vs Java, Go and Rust comparison table | Unmeasured general judgement. The decision record keeps the trade-offs in prose |
| Repeated "nothing is verified" notes | One statement per doc, plus progress in `verification.md` |

No FS, E, R, test ID, checklist row, gate or runbook is removed.

## 8. Implementation approach

1. Work on a new branch, starting from a commit of the current uncommitted docs, so the restructure has a clean diff. This needs your go-ahead when the implementation plan is presented.
2. Add `decisions.md` and `glossary.md` first, because other docs link to them.
3. Build the new docs, applying sections 5 and 6.
4. Remove `payment-system-design.md` and `dotnet-for-payments.md` only after their content has moved. Git history keeps the originals.
5. Run the acceptance checks and report the results.

## 9. Acceptance criteria

Checked with an ad hoc script, which is not added to the repo:

- [ ] All 42 FS, 17 E, 7 R and 5 G IDs and all 108 + 25 checklist rows exist in their home doc.
- [ ] All 66 existing test IDs, plus C34 and C35, are defined once in `verification.md`, and each appears in a build phase.
- [ ] Every test ID cited anywhere is defined.
- [ ] Every relative Markdown link and heading anchor resolves.
- [ ] No references remain to old filenames or to `§` or "section N" of another doc.
- [ ] No SQL block or Mermaid transition block is duplicated across docs.
- [ ] Every decision from section 5 appears in `decisions.md` and its result is reflected in the owning doc.
- [ ] Mermaid diagrams render in a preview.
- [ ] Line totals are reported against section 2.

## 10. Risks

| Risk | Mitigation |
|---|---|
| Content lost during the split | ID and checklist checks; the moved content is compared against the mapping in section 4 |
| A decision changes more text than expected | Decisions are applied only in the owning doc, and other docs link there |
| Large diff is hard to review | Separate baseline commit; implementation plan splits work by destination doc |
| New decisions are mistaken for proven facts | Every value stays marked as a target or starting value until measured |
