# Open Payments Console Design

A responsive web console that lets the business running Open Payments find any payment, understand what happened, and act on it safely.

Status: Draft, awaiting owner review · Last updated: 2026-11-05

The system is defined in the [design docs](../../../README.md#documents), and this spec doesn't restate them. It answers the open item in [Assumptions and limits](../../decisions.md#assumptions-and-limits) that manual review tooling needs a simple internal screen, and it follows the dashboard rule in [engineering standards](../../engineering-standards.md): MFA, RBAC, step-up authentication and an append-only audit log.

## Goal

**Done means:** a staff member can find any payment, say in plain words what happened and where the money is, and take a valid action without any risk of a double charge or double refund. This works at desktop size and in iPhone Safari.

**The console is not on the payment path.** If it is down, slow or under heavy use, payments carry on and the [SLOs](../../architecture.md#goals-and-slos) still hold.

## Users

One deployment serves one self-hosting business and its staff. The people using it are in finance, support and operations. There is no merchant sign-up and no isolation between customers of the console. A deployment is configured with the `merchant_id` values it manages, and staff can see all of them.

## Scope

**In scope for version 1:**

| Screen | Purpose | iPhone |
|---|---|---|
| Needs attention (home) | Queues: `REVIEW`, unresolved payments and refunds past their age limit, resolutions waiting for approval, authorizations about to expire | Full |
| Payments | Look up by ID; list by date range, filtered by state and amount | Full, rows become cards |
| Payment detail | State and money position in plain words; timeline of attempts, PSP events and webhooks | Full |
| Actions | Refund, capture, void, cancel; propose and approve `REVIEW` resolutions | Full |
| Audit log | Who did what, when and why | Readable |
| Staff | People and roles | Desktop only |

**Out of scope for version 1:** revenue analytics, reconciliation and settlement views, webhook and API key management, second approval for large refunds, a native iPhone app, creating payments, and starting new attempts. [Only the merchant or customer starts a new attempt](../../payment-lifecycle.md#attempts), so staff never can. `Proposed` states and partial capture are also out, and the console shows an unrecognized state as its raw name rather than failing.

## Navigation

A left sidebar on desktop. Three bottom tabs on iPhone: Attention, Payments and More. Every payment has a stable URL so it can be shared between staff.

## Roles

| Role | Can do |
|---|---|
| Viewer | Search and read |
| Operator | Viewer, plus refund, capture, void, cancel, and propose `REVIEW` resolutions |
| Approver | Operator, plus approve a resolution proposed by someone else, never their own |
| Admin | Manage staff and roles; no money actions unless also given a money role |

The server checks the role on every request. Hiding a button is never the control.

**Sign-in** is OIDC through the business's identity provider, with MFA required. The console stores no passwords and refuses a session whose token doesn't show MFA was used. Every money action requires re-authentication within the last 5 minutes (a starting value), which can be Face ID on iPhone if the provider supports passkeys. A business with no identity provider can run Keycloak.

## Actions by state

The table follows the [lifecycle transitions](../../payment-lifecycle.md#transitions). An action that isn't valid isn't shown.

| State | Label | Tone | Staff actions | Wording |
|---|---|---|---|---|
| `AWAITING_METHOD` | Waiting for payment | Neutral | Cancel | No charge has been made. |
| `PSP_PENDING` | Sent to PSP | Neutral | None | Waiting for the PSP to answer. |
| `ACTION_REQUIRED` | Waiting for customer | Neutral | None | The customer needs to finish a step such as 3D Secure. |
| `UNKNOWN` | Outcome unknown | Warning | None | We don't yet know if the customer was charged. We're checking automatically. Don't charge again. |
| `REVIEW` | Needs review | Danger | Propose resolution, approve | The PSP's answer disagrees with our record. A person must compare them. |
| `AUTHORIZED` | Authorized | Accent | Capture in full, void | Funds are reserved, not taken. |
| `CAPTURED` | Captured | Success | Refund | Funds were taken from the customer. |
| `PARTIALLY_REFUNDED` | Partly refunded | Success | Refund the remainder or part of it | Part of the captured amount was returned. |
| `REFUNDED` | Refunded | Neutral | None | The full captured amount was returned. |
| `CANCELED` | Canceled | Neutral | None | Ended with no successful attempt. No charge was made. |
| `VOIDED` | Voided | Neutral | None | The reservation was released before capture. |
| `EXPIRED` | Expired | Neutral | None | The authorization window ended without capture. |

An unresolved refund shows as "In progress, amount reserved", never as refunded. The refundable amount shown is `captured_minor - refunded_minor - refund_pending_minor`, the same limit the database enforces.

## Rules for every money action

1. **One key per form.** The server generates the `Idempotency-Key` when it renders the form, so a double-tap or a retry can't repeat the action.
2. **Confirm the effect.** The confirmation step repeats the exact amount, currency and effect, and requires a written reason.
3. **Show conflicts.** On 409, or when the state changed underneath, the console reloads the payment and explains what happened. It doesn't retry with a new key.
4. **Audit first.** See [Audit](#audit).

## Resolving `REVIEW`

1. An Operator or Approver opens the case, sees our record beside the disagreement, and records the PSP's answer and evidence.
2. They propose an outcome the lifecycle allows: for a payment, `AUTHORIZED`, `AWAITING_METHOD` or `CAPTURED`; for a refund, confirm or release.
3. A different Approver approves or rejects the proposal. The server refuses self-approval.
4. On approval, the payment transition module applies the outcome with its ledger effect, using a reversing entry where one is needed, in one transaction with the outbox event.

A late-approved sale can't be voided, so a person reverses it at the PSP. The console records the evidence and never claims to have moved that money. This is a [new decision to record](#new-decisions-needed).

Payments stuck past the age limit ([FS-35](../../failure-scenarios.md)) appear in the same queue, where staff can see the age, the last inquiry and the PSP's answer. **Staff can't change their state in version 1 until the decision below is made.** [FS-35](../../failure-scenarios.md) and runbook R4 say a person resolves them, but the [transitions table](../../payment-lifecycle.md#transitions) has no manual move out of `UNKNOWN`, and anything not listed is forbidden. The proposed answer is to move such a payment from `UNKNOWN` to `REVIEW` on escalation, so that it follows the same two-person flow.

## Architecture

`OpenPayments.Console` is a new ASP.NET Core Razor Pages service with htmx, deployed and scaled separately from the API. The main work is server-rendered, so sign-in stays in secure, HTTP-only cookies and every page works without a client-side application.

| Path | How it works | Why |
|---|---|---|
| Search and queues | Read-only database role on the replica | A heavy search can't slow live payments |
| Read after a staff action | Primary, by payment ID | Staff don't see stale data after acting |
| Refund, capture, void, cancel | The existing [API](../../../api/openapi.yaml), with a per-merchant service credential | Same state rules and idempotency; no second code path that moves money |
| `REVIEW` resolution | New internal endpoint in the payment transition module, not exposed to merchants | State, ledger and outbox stay in one transaction |

**New tables,** in their own `console` schema: `staff` (OIDC subject and role), `audit_log` (append-only), and `review_resolutions` (proposal, evidence, proposer, approver, outcome).

### Search limits

The payment tables must sustain 5,000 payments per second, and they are hash-partitioned, so version 1 adds no broad search index:

- **By ID:** a direct lookup.
- **Lists:** the existing `payments_merchant_created` index, with a required date range of 7 days by default and 31 at most. State and amount filter within that range.
- **Queues:** the existing `payments_unresolved`, `payments_authorized`, `payment_attempts_unresolved` and `refunds_unresolved` indexes.
- **One new index:** a small partial index on `payments` where `state = 'REVIEW'`. `REVIEW` is rare, so its cost should be near zero, but it's [measured before it's accepted](#measurements).

Searching all history would need a separate search store, which is deliberately left out.

### Audit

Because money actions go through the API, the audit record can't share a transaction with them. The console instead writes an intent row, with the staff member, payment, action, amount, reason and idempotency key, **before** calling the API, then writes an outcome row after it. An action therefore never happens without a record. An intent with no outcome after a time limit is flagged in Needs attention and checked by reading the payment. `REVIEW` resolutions write their audit row in the same transaction as the outcome.

The console's database role has `INSERT` and `SELECT` only on `audit_log`.

### Failure behavior

| Situation | What staff see |
|---|---|
| Replica is behind | A "Data as of 14:02:31" banner |
| Replica is down | Search says it's unavailable; lookup by ID still works from the primary |
| API returns 429 or 503 | The form keeps its values and offers a retry with the same key |
| API returns 202 or times out | "Still checking", never "failed" |
| Identity provider is down | Existing sessions can read; money actions wait, because re-authentication can't complete |
| Console is down | Payments are unaffected |

## Visual design

**Direction: Ledger.** Quiet and record-like, with colour reserved for status. It uses UI/UX Pro Max's Swiss minimal style and dense spacing (8-32px). Its sales-page pattern, animation library and amber and violet palette were rejected as unsuited to a staff tool.

| Token | Light | Dark |
|---|---|---|
| Page | `#F7F8FA` | `#11161F` |
| Surface | `#FFFFFF` | `#1A212D` |
| Text | `#172033` | `#EEF2F8` |
| Muted text | `#45516A` | `#A8B4C7` |
| Border | `#D5DAE3` | `#334055` |
| Accent | `#1F5FBF` | `#7FB0F5` |

The theme follows the device setting. Status tones get matching background and text pairs in both themes.

- **Type:** IBM Plex Sans and IBM Plex Mono for IDs, self-hosted so the console makes no calls to Google. Numbers use lining tabular figures and right-align in tables.
- **Status:** always an icon and a label as well as colour.
- **Icons:** one SVG set, self-hosted. No emoji.
- **Motion:** transitions under 200 ms for feedback only, and none under reduced motion.
- **Touch:** targets of at least 44×44 px, with bottom tabs on iPhone respecting safe areas.
- **Tables:** on narrow screens each row becomes a card showing amount, status, ID and time. Pages never scroll sideways.
- **Forms:** visible labels, errors beside the field, and password managers and paste allowed.

## Testing

- **State table:** every `Committed` state against its allowed actions and wording, generated from one table so that the UI and test can't drift.
- **Money safety:** double-submit, racing capture and refund, retry after 429, and 202 handling.
- **Permissions:** every role against every action on the server, self-approval refused, stale re-authentication refused.
- **Audit:** every action produces intent and outcome rows, and a killed request leaves a flagged intent.
- **Accessibility:** automated checks, keyboard use, and contrast of at least 4.5:1 in both themes.
- **Devices:** desktop, and iPhone Safari at 390px on the iOS Simulator.

## Measurements

These are design dependencies, and they need results before the build relies on them:

| Item | Pass condition |
|---|---|
| Partial index on `REVIEW` | No measurable change in sustained throughput or p99 in the [benchmark](../../verification.md) |
| Console searches on the replica during a payment benchmark | Payment p99 target still met; replica lag stays below the banner's threshold |

## New decisions needed

Record these in [Decisions](../../decisions.md) once this spec is approved, continuing from D39:

1. **The console is a separate deployable** that reads from the replica and moves money only through the API or the internal resolution endpoint.
2. **`REVIEW` resolution requires two people** and uses a new internal endpoint. This closes "Review tooling stays `Open`" in [D12](../../decisions.md#d12).
3. **Audit uses intent and outcome rows** for actions that go through the API.
4. **Search is limited to a 31-day range** in version 1.
5. **Reversals made at the PSP are recorded with evidence,** because the console can't make them itself. This defines what the ledger records for a late-approved sale that a person reverses.
6. **Add `UNKNOWN` to `REVIEW` on escalation past the age limit** to the lifecycle. Unresolved capture attempts and refunds need the same treatment on their own rows. This needs the owner's decision, because it changes the state machine.

## Starting values to confirm

These are starting values, not measurements, and can be changed by configuration: re-authentication window of 5 minutes, default list range of 7 days, maximum of 31 days, the lag threshold for the banner of 60 seconds, the authorization-expiry warning of 24 hours, and the audit intent limit of 10 minutes. The age limits for the queue come from the existing [timers](../../payment-lifecycle.md#timers).

## Delivery

After approval, the [implementation plan](../plans) and a clickable prototype follow. Under the [delivery workflow](../../delivery-workflow.md), the build goes to DeepSeek and an independent review goes to a fresh Opus session. The build doesn't start until the owner has approved the design record and the measurements above have results.
