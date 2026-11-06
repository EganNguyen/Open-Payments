# Stage C Design Gate

The approval record for the staff console: what the owner has approved, and what must finish before the build starts. The console is no longer a separate stage. Its tasks are built in stages 1 and 2 ([D49](../decisions.md#d49)), and this record stays their design gate.

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
| A frontend task in every stage ([D49](../decisions.md#d49)) | 2026-11-06 |

## Decided in planning, for the owner to confirm

Claude changed or added these after the spec was approved, because of what the code showed.

| Item | Cost if wrong |
|---|---|
| The timeline has no PSP events, webhook deliveries or last inquiry yet, because the data isn't linked or stored | Staff check those at the PSP until later stages add them |
| Four new indexes instead of one, adding attempt and refund `REVIEW` queues and `refunds (payment_id)` | Slower refund writes, which Task C2 measures |
| New commands for refund resolution, and `ManualResolution` also moves the attempt | Changes `Decide`, so Stage 2's review must recheck it |
| A stuck void isn't escalated, because no transition moves `REVIEW` to `VOIDED` | A person handles a stuck void at the PSP, with no console record |
| A capture attempt escalated to `REVIEW` can't resolve to `AWAITING_METHOD` | A rare case escalates to a design session |
| The resolution transaction may exceed the five-statement guideline | Slightly longer locks on a rare path |
| Cookies use `SameSite=Lax`, because `Strict` breaks the return from sign-in | Relies on antiforgery for POSTs |
| Stage 1 has no Needs attention screen, and its home page is Payments | Staff can't see a work queue until Stage 2 |
| In Stage 1, `REVIEW` shows no resolution controls | A `REVIEW` case waits for Stage 2 or is handled outside the console |
| In Stage 1, audit intents with no outcome are marked only in the audit log | Someone has to look there, because no queue flags them until Stage 2 |
| C4's migration, including `review_resolutions`, ships in Stage 1, before any code uses that table | An unused table until Stage 2, and a new migration if C5 changes its shape |

## Must finish before the build

| ☐ | Item | Needs |
|---|---|---|
| ☑ | Task C1: decisions recorded and docs corrected | Done with this record |
| ☑ | Where the console goes in the stage order | Answered by [D49](../decisions.md#d49) |
| ☐ | Task C2: index measurement | Stage 1 baseline workload; a before-and-after comparison on the owner's laptop, approved by the owner 2026-11-06. It counts as a preview |
| ☐ | Task C2: clickable prototype reviewed | The owner |
| ☐ | Owner signs this record, naming the starting commit | The owner |

## Risks to watch

- The API contract stays a draft until C39, so the console's API client may need changes after that.
- Late-approved sale reversals can leave settlement lines with no match ([D44](../decisions.md#d44)). This is settled when settlement reconciliation is designed.
- OIDC providers report MFA and passkeys differently, so `RequiredAmr` needs checking against each real provider.
- The index measurement is a laptop preview, because [E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) says no Linux host exists yet. It can show a large cost, but not prove there is none on benchmark hardware. Repeat it on the first Linux run, and if the indexes cost more there, bring the result to the owner.
