# OP-BENCH Design Gate

The approval record for the end-to-end benchmark and reliability suite: what the owner approved, what was decided without the owner, and what the build must not change.

Status: **Approved, 2026-11-09** · The build starts from the commit that adds this record · Last updated: 2026-11-09

The design is the [benchmark suite spec](../superpowers/specs/2026-11-09-benchmark-suite-design.md). The tasks are in the [build plan](../superpowers/plans/2026-11-09-benchmark-suite.md). The rules are in [Delivery workflow](../delivery-workflow.md#design-gate).

## Approved by the owner

| Item | Record |
|---|---|
| 500 completed payments/s laptop milestone at step 6; target stays 5,000 | [D51](../decisions.md#d51) |
| Laptop results are `PREVIEW` only | D51 |
| Latency gated on measured platform overhead, p50 ≤ 25 ms, p99 ≤ 100 ms, using each request's PSP wait | D51 |
| Recovery: first success ≤ 30 s, SLO ≤ 2 min after removal; times from injection reported too | D51 |
| D47 mix and k6 harness unchanged | D51 |
| CK11, widened to the state machine; PSP effects stay in CK2 | [D52](../decisions.md#d52) |
| New IDs TX1, TX2, C52-C55, CK11 | [Verification](../verification.md) |
| Five refinements: full PSP lifecycle, wider CK11, two separate deadlock tests, crash recovery `INCONCLUSIVE` until Stage 2, `pg_stat_statements` in preflight | Spec §4.3, §4.5, §4.6, §5.3 |
| Verdict separate from environment label; every result reproducible from its manifest | Spec §7, §8.1 |
| Build order P0-P8 | Spec §10 |

## Decided without the owner

Confirm or change these before the build passes P1.

| # | Decision | Reason |
|---|---|---|
| W1 | **The suite is built inside Stage 1, as part of Task 17**, not as a new stage. | The delivery workflow forbids starting a new stage before Stage 1's review closes. The suite's evidence is what Task 17's laptop limit search needs anyway. |
| W2 | Lifecycle case **L0 (never started)** added beside the owner's five cases. | Without it, a request that died before transaction A would fail CK11 although nothing is wrong. |
| W3 | Case **L5 gets its own ID, C55**. | The owner listed it as a critical case and no existing test covers it. |
| W4 | The environment label for production-grade evidence is **`RECORDED`**, the repository's existing word, not "PRODUCTION". | A Linux Phase 3 run is not production. Reusing the existing term keeps one trust rule. |
| W5 | **C5, C6 and C36 are reused** from `OpenPayments.Acceptance.Tests`, not rewritten. | They already exist and pass. |
| W6 | **`op-bench report` also writes `suite.html`** ([plan P7b](../superpowers/plans/2026-11-10-op-bench-html-report.md), [prototype](../design/op-bench-report/prototype.html)), and the §8 contract gains a `gates` array and a fixed `runs[]` shape. Proposed 2026-11-10; **confirmed by the owner, in chat, 2026-11-10**. Build after P7. | The page needs per-gate results and per-run values that §8 leaves unspecified. Both are already computed for `suite.md` and `verify`; nothing new is measured. |

## Must not change during the build

D28-D50 and D38; the append-only ledger and CK1 manifest; `benchmarks/k6/mix.js`, `steps.json` and the D47 shares; `TxRunner`'s retry policy and D28 commit handling; CK1-CK10; `db/migrations/`. The `txlab` schema exists only inside the reliability test fixture. A builder who finds one of these in the way stops and escalates in `docs/gates/stage-1-escalations.md`.

## Risks carried into the build

- One laptop runs everything. At step 6 the SSD may limit first; the suite reports that as the first failing resource, not as the application's capacity.
- Until Stage 2, the full suite's expected result is `INCONCLUSIVE (PREVIEW)`.
- The lock sampler can't see waits under 100 ms.
- L2 depends on the mock's `ApproveThenHangRate` writing the effect before it hangs. P5's first test proves that, rather than assuming it.

## Sign-off

| Approved by | Date | Build starts from commit |
|---|---|---|
| EganNguyen, in chat, recorded by Claude | 2026-11-09 | The commit that adds this record |
