# Stage 1 Console UX Plan

A UX review of the Stage 1 console as built (Task 18), and the fixes to make before Stage 1's review gate.

Status: **Draft, for the owner to approve** · Last updated: 2026-11-09

> **For whoever executes this:** Follow [Delivery workflow](../../delivery-workflow.md) and the console plan's [Global Constraints](2026-11-06-console.md#global-constraints). Nothing here changes the spec's state table, wording, money rules or roles. Where a fix would, it is marked **Owner** and waits for a decision.

## How it was reviewed

The live console at `localhost:8090`, signed in as the local Keycloak `admin@console.local` (role Admin), against the Stage 1 stack seeded by the load harness. Payments, Payment detail, Audit log, Staff and More were walked at 1280×800 and 375×812. Templates were read for the action pages. Checked against the [console spec](../specs/2026-11-05-console-design.md) and UI/UX Pro Max's accessibility, touch, layout, forms and navigation rules.

**Not exercised live:** the money actions, re-authentication, 409 conflict, 202 "still checking" and the replica-lag banner, because the signed-in account is Admin with no money role. Task U6 covers them.

**Out of scope:** Needs attention and `REVIEW` resolution, which arrive with F2 in Stage 2.

## What already works

- Status is always an icon and a label as well as colour, with the spec's labels and sentences ("Funds are reserved, not taken.").
- Tabular, right-aligned amounts; IDs in Plex Mono; stable payment URLs.
- iPhone: bottom tabs, result rows become 281×44 px links, no sideways scroll.
- Action forms: visible labels, errors beside the field with `role="alert"`, a written reason, the effect repeated before confirming, and a server-issued key per form.
- 34 browser tests with no axe violations.

## Findings

Ranked by how much they can mislead a staff member about a payment or the money.

| # | Where | Finding | Severity |
|---|---|---|---|
| F1 | Every page | Nobody can see who they're signed in as, or their role. An Admin opening an `AUTHORIZED` payment sees no Capture or Void and no reason why, which reads as "nothing can be done". | High |
| F2 | Payment detail | The money block is the same for every state. `AUTHORIZED` shows "Refundable 0.00 USD" and "Captured 0.00 USD" but never "Reserved 48.97 USD", which is the one number that matters there. | High |
| F3 | Payment detail | A succeeded authorize attempt shows "PSP reference: None yet". The attempt row has an empty reference, so "None yet" implies one is still coming. Needs a check of whether the mock PSP or the API leaves it empty. | Medium, plus a data check |
| F4 | Payment detail | Merchant is a raw UUID. A deployment manages several merchants, and the list has no merchant column or filter. | Medium, **Owner** |
| F5 | Payments, iPhone | The filter form fills the whole first screen (ID, From, To, State, Currency, Min, Max, Search). Results start below the fold. | Medium |
| F6 | Payment detail, iPhone | Seven stacked label/value pairs push the timeline, and any action buttons, below the fold. | Medium |
| F7 | Payments | The State filter is ordered by raw code, so "Waiting for customer" comes first and "Needs review" and "Outcome unknown" sit near the end. | Low |
| F8 | Payments | No count or summary of what's shown. Paging is "Older payments" only, with no way back to newer. | Low |
| F9 | Payments, desktop | Only the 36-character ID is a link. The row isn't, and the ID can't be copied in one action. | Low |
| F10 | Audit log | "Person (staff ID)" asks for an internal ID that staff never see. | Low |
| F11 | Staff | Roles show as raw lowercase codes with no description. The Admin's own row offers "Disable" and a role change, so the only Admin can lock everyone out of Staff. "Identity-provider subject" has no help. | Medium |
| F12 | More, iPhone | The panel starts with an empty gap, and it is where identity and role (F1) belong. | Low |

## Tasks

Each task is small, server-rendered and tested at 1280 and 390 px with axe, like Task 18.

### U1: Identity and role (F1, F12)

- [ ] Sidebar footer above Sign out, and the top of More: the staff email and role in words ("Admin · manages staff").
- [ ] Payment detail: when the state has staff actions but the role allows none, show one muted line, for example "Capture and void need the Operator role." The server check is unchanged.
- [ ] Test: a Viewer and an Admin see the line on `AUTHORIZED` and `CAPTURED`; an Operator sees the buttons and no line.

### U2: State-aware money block (F2, F3)

- [ ] Show the rows that mean something in each state, from one table beside `StatePresentation` so the UI and tests can't drift:

| State | Rows |
|---|---|
| `AWAITING_METHOD`, `PSP_PENDING`, `ACTION_REQUIRED`, `UNKNOWN`, `REVIEW` | Amount |
| `AUTHORIZED` | Amount, Reserved, and the authorization expiry if stored |
| `CAPTURED`, `PARTIALLY_REFUNDED`, `REFUNDED` | Amount, Captured, Refunded, In progress if any, Refundable |
| `VOIDED`, `CANCELED`, `EXPIRED` | Amount |

- [ ] PSP reference: "None yet" only while an attempt is unresolved; "Not recorded" once an attempt has succeeded without one. Show each attempt's reference on its timeline line, as the spec's timeline section already says.
- [ ] Check why the seeded `AUTHORIZED` payment's succeeded attempt has no `psp_reference`. If the API or mock PSP drops it, that is a Stage 1 escalation, not a UX fix.
- [ ] Test: the state table against every `Committed` state.

### U3: iPhone layout (F5, F6)

- [ ] Payments: ID lookup stays on top. The other filters go into a `<details>` whose summary reads the current filter, for example "Last 7 days · USD · Any state". It opens when a filter has an error. Results follow directly.
- [ ] Payment detail: status sentence, then the action buttons, then the money block as a two-column list, then the timeline. Buttons stay in the page flow, not sticky, so a money action is never under a thumb by accident.
- [ ] Test: at 390 px the first result card and, on detail, the first action button are within the first 812 px.

### U4: List polish (F7, F8, F9)

- [ ] Order the State filter by attention: Needs review, Outcome unknown, then lifecycle order. Labels unchanged.
- [ ] A summary line above the table: "50 payments, newest first, 3 Nov to 9 Nov". Add "Newer payments" when not on the first page.
- [ ] Desktop: the whole row opens the payment (one link stretched over the row, so it stays one tab stop). A copy button beside the ID, labelled "Copy payment ID".

### U5: Audit and Staff (F10, F11)

- [ ] Audit: filter by staff email from a list, not by ID.
- [ ] Staff: roles in words with one line each, from the spec's role table. Hide Disable and role change on your own row, and refuse on the server the change that would leave no active Admin. "Identity-provider subject" gets help text: "The `sub` claim from your identity provider."
- [ ] Test: the last Admin can't be disabled or demoted, on the server.

### U6: Walk the money actions live

Not a code change: the evidence missing from this review.

- [ ] Add a local Operator and an Approver to `deploy/compose/console/realm.json` and `console.staff`, with local-only values.
- [ ] Walk refund, capture, void and cancel at both widths, including stale re-authentication, a 409 from a racing capture, a 202 from a PSP timeout, and the lag banner with standby1 paused.
- [ ] Record what is seen. Anything that departs from the spec's [Failure behavior](../specs/2026-11-05-console-design.md#failure-behavior) becomes a finding here.

## Owner decisions

| Question | Recommendation | Blocks |
|---|---|---|
| F4: show merchants by name? | Yes: a display name per configured `merchant_id`, in console configuration, shown on detail and as a list filter when more than one is configured | Part of U4 |
| Does this plan go to DeepSeek as a Stage 1 task before the review gate, or into F2? | Before the review gate: F1 and F2 can mislead staff about money, and U1-U5 are small | When the build starts |

## Order

U2 and U1 first, because they change what staff believe about a payment. Then U3, U6, U4 and U5. U6 can run any time, and its findings may add to U2 or U3.
