# Stage C Design Gate

The approval record for the staff console: what the owner has approved, and what must finish before the build starts.

Status: **Open** · No starting commit yet · Last updated: 2026-11-06

The rules are in [Delivery workflow](../delivery-workflow.md#design-gate). The work is defined by the [spec](../superpowers/specs/2026-11-05-console-design.md) and the [plan](../superpowers/plans/2026-11-06-console.md).

## Approved by the owner

| Item | When |
|---|---|
| Console for the self-hosting business, with money actions and safeguards | 2026-11-05 |
| Razor Pages and htmx, in a separate service | 2026-11-05 |
| Ledger visual direction | 2026-11-05 |
| [Console spec](../superpowers/specs/2026-11-05-console-design.md) | 2026-11-05 |
| [D40-D45](../decisions.md#d40), including `UNKNOWN` to `REVIEW` on escalation | 2026-11-05 |

## Decided in planning, for the owner to confirm

Claude changed or added these after the spec was approved, because of what the code showed.

| Item | Cost if wrong |
|---|---|
| The timeline has no PSP events, webhook deliveries or last inquiry yet, because the data isn't linked or stored | Staff check those at the PSP until later stages add them |
| Four new indexes instead of one, adding attempt and refund `REVIEW` queues and `refunds (payment_id)` | Slower refund writes, which Task C2 measures |
| New commands for refund resolution, and `ManualResolution` also moves the attempt | Changes `Decide` after Stage 1's review, so Stage C's review must recheck it |
| A stuck void isn't escalated, because no transition moves `REVIEW` to `VOIDED` | A person handles a stuck void at the PSP, with no console record |
| A capture attempt escalated to `REVIEW` can't resolve to `AWAITING_METHOD` | A rare case escalates to a design session |
| The resolution transaction may exceed the five-statement guideline | Slightly longer locks on a rare path |
| Cookies use `SameSite=Lax`, because `Strict` breaks the return from sign-in | Relies on antiforgery for POSTs |

## Owner decisions needed

| Decision | Options | Recommendation |
|---|---|---|
| **Where Stage C goes in the stage order.** The workflow runs stages one at a time, and its Task C3 and Stage 2 both change payment code | Alongside Stage 2 once Stage 1's review closes, or after Stage 2 closes | Alongside Stage 2, with Task C3 done before Stage 2's escalation job |

## Must finish before the build

| ☐ | Item | Needs |
|---|---|---|
| ☑ | Task C1: decisions recorded and docs corrected | Done with this record |
| ☐ | Task C2: index measurement | Stage 1 baseline workload; a run on the benchmark machine |
| ☐ | Task C2: clickable prototype reviewed | The owner |
| ☐ | The order decision above | The owner |
| ☐ | Stage 1's review gate closed | Stage 1 |
| ☐ | Owner signs this record, naming the starting commit | The owner |

## Risks to watch

- The API contract stays a draft until C39, so the console's API client may need changes after that.
- Late-approved sale reversals can leave settlement lines with no match ([D44](../decisions.md#d44)). This is settled when settlement reconciliation is designed.
- OIDC providers report MFA and passkeys differently, so `RequiredAmr` needs checking against each real provider.
