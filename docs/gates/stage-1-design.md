# Stage 1 Design Gate

The approval record for Stage 1: what the owner has approved, and what must finish before the build starts.

Status: **Open** · Last updated: 2026-11-05

Stage 1 is the spike, test tools, payment core and first ramp steps. The rules are in [Delivery workflow](../delivery-workflow.md#design-gate).

## Approved by the owner

| Item | When |
|---|---|
| [D25](../decisions.md#d25): hash-partition the hot tables, with most data retiring by batched delete | 2026-11-05 |
| [D26](../decisions.md#d26): G1 stays open for Linux, and the build continues on one cell | 2026-11-05 |
| [D21-D24](../decisions.md#d21): accounts and fund flows, pure `Decide`, added checks and log line, corrected gates | 2026-11-05 |
| [D35](../decisions.md#d35): merchants use their own PSP accounts | 2026-11-05 |
| [D37](../decisions.md#d37): void and authorization expiry | 2026-11-05 |
| [D38](../decisions.md#d38): refund lock deferred | 2026-11-05 |
| [D39](../decisions.md#d39): PSP-hosted card fields, vault deferred | 2026-11-05 |

**Approved together on 2026-11-05.** Claude decided these first, and the owner then approved all of them.

| Item | Cost if wrong |
|---|---|
| [D27](../decisions.md#d27): a negative PSP answer needs proof | `UNKNOWN` lasts longer than necessary |
| [D28](../decisions.md#d28), as amended: no cancel during `COMMIT`, with a 3 s wait limit | Writes stop while both standbys stall; the value may need tuning |
| [D29](../decisions.md#d29): auto-capture is one PSP call where supported | A merchant can't rely on `payment.authorized` coming first |
| [D30](../decisions.md#d30): callbacks apply atomically, and count as load | More commits per payment |
| [D31](../decisions.md#d31): one key store, and a defined answer for every replay | A contract change later |
| [D32](../decisions.md#d32): deterministic event and ledger IDs | One more column |
| [D33](../decisions.md#d33): key check before fraud; fraud blocks recorded | One more read per request |
| [D34](../decisions.md#d34): separate limits, fixed timer order | More values to tune |
| Capture posts two ledger transactions, a hold release and the money, so real and logical never mix (D21) | A schema change before the first migration |
| A "refund released" row was added to the fund flows | One table row |
| E14 and E15 moved to a Phase 3 group, C31c | A gate moves one phase |
| Auto-capture is a request field, `capture_mode` | A contract edit |
| The attempt cap answers 409 with `attempt_cap_reached` | A contract edit |
| Late voids get their own unique index, so one can't block a capture (D37) | A schema change before the first migration |
| A breaker per PSP and per account, so a revoked credential fails one merchant (D35) | More state to track |
| PgBouncer is built from Alpine's package, and images are pinned | A Dockerfile swap |
| Merchant events carry IDs, state and changed amounts ([catalog](../event-catalog.md#thin-or-full-events)) | Shape changes before Stage 3 |

## Must finish before the build

| ☐ | Item | Needs |
|---|---|---|
| ☑ | Task 1: design docs corrected | Done, `f756607` |
| ☑ | Task 2: API contract and event catalog drafts | Done, `ae5847d`, updated since |
| ☑ | Task 3: replicated stack, and [commit behavior observed](../../benchmarks/spike/commit-cancel.md) | Done, `108a626` |
| ☑ | Task 4: spike schemas and workloads | Done, `511c8d9` |
| ☑ | Task 5: [spike runner](../../benchmarks/spike/README.md), proven by a local dry run | Done, `7ceb0dd` |
| ☑ | Task 6, as far as the owner chose: [MacBook spike](../benchmarks/spike/macbook-2026-11-05/summary.md), with database-only runs stopped on 2026-11-05 to benchmark the full platform once built. [D25](../decisions.md#d25) and [D26](../decisions.md#d26) approved, and `db/migrations/0001_initial.sql` passes `scripts/check-migration.sh` | Done |
| ☑ | G1 stays open, and closes on Linux before Stage 4 ([D26](../decisions.md#d26)) | Accepted by the owner, 2026-11-05 |
| ☐ | Install .NET 10 | Owner, before Task 7 |
| ☐ | Sign below, naming the build's starting commit | Owner |

## Open, not blocking this gate

These block later stages. Each has a recommendation in the [spec](../superpowers/specs/2026-11-05-implementation-and-benchmark-design.md#owner-decisions).

- D36: is 5,000/s a peak or an average? Blocks L2 and G6, in Stage 4.
- Which PSP sandbox, and one PSP or two? Blocks Stage 2.
- Does v1 carry real money? Who owns the fraud fallback and shed order? How long is data kept? Blocks Stages 4 and 5.

## Findings settled by D25

Task 4 and the MacBook spike found these, and [D25](../decisions.md#d25) now covers them.

- **The outbox had no way to find unpublished rows.** The spike adds a partial index, `outbox_unpublished`, which isn't in the design.
- **Time-partitioning forces two key changes (schema B).** `attempt_id` stops being unique alone, and `source_event_id` can't be globally unique, so a ledger transaction's ID must be derived from its source event ([D32](../decisions.md#d32)).

## Risks carried into the build

- Whether Npgsql sends a cancel when its own timeout fires on a commit. Task 13 must settle it, and an answer that breaks D28 is an escalation.
- Production throughput numbers remain estimates. The [MacBook summary](../benchmarks/spike/macbook-2026-11-05/summary.md) labels each as measured, extrapolated, assumed or unverified.
- Most spike variants, including the full mix (V13) and two cells, weren't measured.
- Schema C has one measured run, and its read cost across partitions isn't measured.
- The build starts before G1 is answered. If Linux requires cells, Stage 4 gets replanned ([D26](../decisions.md#d26)).

## Sign-off

The owner fills this in when the gate closes.

| Approved by | Date | Build starts from commit |
|---|---|---|
| | | |
