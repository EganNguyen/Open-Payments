# Stage C Design Gate

The approval record for the staff console: what the owner has approved, and what must finish before the build starts. The console is no longer a separate stage. Its tasks are built in stages 1 and 2 ([D49](../decisions.md#d49)), and this record stays their design gate.

Status: **Signed** 2026-11-09 · Build starts from `e4bff52` · Last updated: 2026-11-09

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
| Four new indexes instead of one, adding attempt and refund `REVIEW` queues and `refunds (payment_id)`, which now ships earlier under [D50](../decisions.md#d50) | Slower refund writes; Task C2 found no cost in its valid runs |
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
| ☑ | Task C2: index measurement | Stopped early by the owner 2026-11-06, with three valid runs and no cost detected. It counts as a preview. See [the result](../../benchmarks/console-indexes.md) |
| ☑ | Task C2: clickable prototype reviewed | The owner reviewed the published prototype, [Open Payments Console](https://claude.ai/artifact/EwVgpP7vZVeV5X812UkyHr), on 2026-11-09. The repository copy in `docs/design/console-prototype/` doesn't render on its own, because its `support.js` runtime isn't in the repository |
| ☑ | Owner signs this record, naming the starting commit | Signed below |

## Risks to watch

- The API contract stays a draft until C39, so the console's API client may need changes after that.
- Late-approved sale reversals can leave settlement lines with no match ([D44](../decisions.md#d44)). This is settled when settlement reconciliation is designed.
- OIDC providers report MFA and passkeys differently, so `RequiredAmr` needs checking against each real provider.
- The index measurement is a laptop preview, because [E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) says no Linux host exists yet. It can show a large cost, but not prove there is none on benchmark hardware. The owner decided on 2026-11-06 not to repeat the comparison on Linux. The first Linux run confirms with `EXPLAIN` that the queries use these indexes and watches `refunds_payment`'s cost in the normal results. If that cost looks significant, bring it to the owner.

## Sign-off

| Approved by | Date | Build starts from commit |
|---|---|---|
| EganNguyen, in chat, recorded by Claude | 2026-11-09 | `e4bff52` |

Signing confirms the items under "Decided in planning, for the owner to confirm". Task 18 is built by Opus, under the owner's 2026-11-09 exception to the rule that DeepSeek builds, so the review gate must be a fresh session.
