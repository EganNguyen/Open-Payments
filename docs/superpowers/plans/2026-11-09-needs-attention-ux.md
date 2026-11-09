# Needs Attention Page Plan

The UI and UX of the console's home page, Needs attention: what it shows, in what order, for whom, and how it behaves at desktop and iPhone sizes.

Status: **Approved by the owner; N1 and N2 built** · Last updated: 2026-11-09

> **For whoever executes this:** This page is built in Stage 2 by [Task C12](2026-11-06-console.md#task-c12-needs-attention-and-review-resolution), as part of [F2](2026-11-05-open-payments-implementation-and-benchmark.md#frontend-tasks-by-stage). This plan adds the page's layout and behaviour to C12. It doesn't change C12's queue sources, the review flow, the roles or any wording in the [spec](../specs/2026-11-05-console-design.md). Items marked **Owner** wait for a decision. The console plan's [Global Constraints](2026-11-06-console.md#global-constraints) apply.

## Purpose

**Done means:** a staff member opening the console sees, within one screen, whether anything needs a person, what it is, how old it is, and whether they personally can act on it. When nothing needs attention, the page says so and nothing else.

This is a work queue, not an analytics dashboard. Volume, revenue and success-rate charts stay out of version 1, as the spec says.

## Starting points

- **Spec:** the queues are in [Scope](../specs/2026-11-05-console-design.md#scope), the review flow in [Resolving REVIEW](../specs/2026-11-05-console-design.md#resolving-review), and the stale-data banner in [Failure behavior](../specs/2026-11-05-console-design.md#failure-behavior).
- **Prototype:** `docs/design/console-prototype/Main.dc.html` has the screen, five queues with their titles and help text, and example rows.
- **Today:** `Pages/Index.cshtml.cs` redirects to Payments. C12 replaces it.
- **Data, all present after Stage 1:** the indexes `payments_review`, `payment_attempts_review`, `refunds_review`, `payments_unresolved`, `payment_attempts_unresolved`, `refunds_unresolved`, `payments_authorized` on `authorized_until`, the `console.review_resolutions` table, and `AuditWriter.FindOrphanIntentsAsync`.

## Page structure

From top to bottom:

1. **Title and freshness.** "Needs attention", then "Checked 14:02:31 UTC" with a Refresh button, and the existing `_DataAsOf` banner when the replica is behind.
2. **Summary.** One line per queue, with its count, linking to that queue's section. It reads as a table of contents, so on iPhone the whole state fits on the first screen.
3. **Queue sections.** Only the queues that have rows, in the fixed order below, each with its title, count, one help sentence and up to 10 rows.
4. **All clear.** The queues with no rows, named in one muted line: "Also clear: Waiting for approval, Authorizations ending soon."

When every queue is empty, none of items 1-4 is shown. The page is one calm panel and nothing else: "Nothing needs attention." with the check time. There is no title line, freshness line or Refresh button above it, so an all-clear page can't be mistaken for work.

### Why this differs from the prototype

The prototype draws all five queue panels, empty ones included, each saying "Nothing here needs you right now." On a normal day most queues are empty, so the page would be five panels of reassurance that push the one real item down. Collapsing the empty ones to a single line keeps the same information and puts the work first.

## Queue order and rows

The order is fixed, so staff learn where things are. It goes from "a person must decide" to "the system is still handling it".

| Order | Queue | Row shows | Sorted by | Who can act from the row |
|---|---|---|---|---|
| 1 | Needs review | Amount, the disagreement in words (for example "PSP answered 301.50 USD, we expected 310.50 USD"), what is in review (payment, refund or attempt), age | Oldest first | Operator and Approver: Propose |
| 2 | Waiting for approval | Amount, proposed outcome, who proposed it, age | Oldest first | Approver, unless they proposed it: Decide |
| 3 | Actions with no recorded result | Action and amount, staff member, minutes since it started | Oldest first | Nobody acts; open the payment and check it |
| 4 | Unresolved too long | What is unresolved (payment, capture, refund or void) and for how long | Oldest first | Nobody: "We're checking automatically" |
| 5 | Authorizations ending soon | Amount, time left ("ends in 9 h") | Ending soonest first | Operator: Capture or Void, on the payment page |

Every row also shows the merchant, the short payment ID, and one target that opens the payment. Rows are at least 52 px tall on desktop and 44 px on iPhone, and the whole row is the link.

**Ages and time left** are relative ("3 h 12 min"), with the exact UTC time in a `<time>` element's `datetime` and `title`. Ages past twice the queue's limit use the Warning tone, with an icon and the words "Over limit" as well as colour.

**A stuck void** in Unresolved too long says "This console can't settle a stuck void. Handle it at the PSP." It follows the spec's [correction 4](../specs/2026-11-05-console-design.md#corrections-from-planning).

## Role-aware behaviour

The server decides what each person can do, and the page only says it.

- **Counts and badge** count rows the person can act on. A Viewer's badge shows nothing, but the page still lists every queue.
- **A row the person can't act on** says why, in muted text, once per section: "Proposing needs the Operator role." or "You proposed this, so another Approver decides."
- **Your own proposal** in Waiting for approval is marked "Yours".
- **Admin** with no money role reads everything, with the same note. This uses the identity line from [U1](2026-11-09-stage-1-console-ux.md#u1-identity-and-role-f1-f12).

## Navigation

- `/` is Needs attention. The sidebar gets "Needs attention" first, and the iPhone tabs become Attention, Payments and More, as the spec says.
- **Badge:** the actionable count on the sidebar item and the Attention tab, shown as "99+" above 99, with an accessible name such as "Needs attention, 7 to act on". It is read when the page loads, not polled.
- **Deep links:** each section has an anchor (`/#review`) and its own full list (`/attention/review`) when it has more than 10 rows. Opening a row goes to the payment page with `?from=review`, which shows a case panel at the top, explaining why it is in the queue and what to do, and a link back to the same section.

## Freshness and refresh

- Queues read from the replica, like search. The check time is the replica's time, minus its lag.
- **No automatic refresh in version 1.** Rows that move while someone reads or tabs through them cause mis-clicks, and a screen reader loses its place. Refresh is a button, and it reloads the queue sections with htmx, keeping the scroll position.
- After a staff action, the payment page reads from the primary, as the spec says. Back on Needs attention, a row that may be stale says "Updated just now. This list may take a moment to catch up." instead of hiding the row.

## Large counts

A PSP outage can put hundreds of payments in Unresolved too long at once.

- Each section shows at most 10 rows. Counts stop at 1,000, shown as "1,000+", so the count query is bounded.
- When Unresolved too long holds more than 50 rows, the section starts with one line: "Many payments are unresolved at once. This usually means a PSP problem, not 50 separate ones." The 50 is configuration. **Owner:** whether it should also link to the PSP's status page, configured per PSP.

## iPhone

- The summary lines come first. Each queue section is a `<details>` element, open when it has rows. Rows become cards showing amount, reason, age and short ID.
- No horizontal scrolling. Long reasons wrap to two lines and then end with an ellipsis, and the full text is on the payment page.
- The bottom tabs respect the safe area, and the badge doesn't cover the tab label.

## Accessibility

- One `h1`, one `h2` per queue, with the count inside the heading ("Needs review, 3").
- Status and over-limit states use an icon and words as well as colour.
- Refresh announces its result in a polite live region: "Checked 14:05:10. 2 need attention."
- axe at 1280 and 390 px in light and dark, keyboard order title → refresh → summary → sections, and visible focus on every row.

## Tasks

These extend C12 and run in its build. Each is server-rendered and tested like Task 18.

### N1: Queue reads and the page

- [x] `QueueReader.ReadAsync` returns, per queue, up to 10 rows and a count capped at 1,000, read from the replica with the lag. Every query uses the literal state, so its partial index is used. `EXPLAIN` test for each.
- [x] `Index.cshtml` with title, freshness, summary, sections with rows, the all-clear line and the empty state.
- [x] Tests: each queue's order; empty queues collapse into the all-clear line; all empty gives one panel; 1,001 rows show "1,000+".

### N2: Roles, badge and navigation

- [x] The actionable count per role, the badge, the "why you can't act" notes and "Yours".
- [x] Sidebar and tabs updated; `/` serves the page; Payments moves second.
- [x] Tests: Viewer, Operator, Approver and Admin each see the right badge count and notes; an Approver's own proposal isn't counted for them.

### N3: Queue lists and the case panel

- [ ] `/attention/{queue}`: the full list, paged by age, 50 at a time.
- [ ] The payment page's case panel for `?from=`, with wording for each queue taken from the prototype's case and stuck texts.
- [ ] Tests: the back link returns to the same anchor; an unknown `from` value shows no panel.

### N4: Refresh, large counts, iPhone and accessibility

- [ ] htmx refresh of the sections, with the live region; works without JavaScript as a full reload.
- [ ] The many-unresolved line at the configured threshold.
- [ ] Browser tests at 390 px: the summary and the first row of the first non-empty queue fit within 812 px. axe in light and dark.

## Owner decisions

The owner accepted all four recommendations in chat on 2026-11-09.

| Question | Decision |
|---|---|
| Collapse empty queues into one line, instead of the prototype's five panels? | Yes |
| Automatic refresh? | No in version 1; a Refresh button |
| Link to the PSP's status page when many payments are unresolved? | Yes, if the PSP has one; configured per PSP (N4) |
| Should the badge count only what the person can act on? | Yes |

## Build record

**Who built it:** Opus, on 2026-11-09, under an owner exception given in chat to the rule that DeepSeek builds, for N1 and N2 only. It is Stage 2 work built before Stage 2's design gate, because both tasks only read. The review must be a fresh session that didn't write this code.

| Item | Result |
|---|---|
| Tests first | `AttentionTests`: 29 tests. Against an empty `QueueReader` stub, 27 failed and 2 passed: "no badge for Viewer or Admin", which held because no badge existed yet |
| Console tests | `dotnet test tests/OpenPayments.Console.Tests`: 152 passed. Stage 1's `Home_opens_payments_until_attention_exists` was removed, because the home page is now Needs attention |
| Browser tests | `dotnet test tests/OpenPayments.Console.Browser.Tests`: 40 passed. Needs attention joined the screen matrix: axe in light and dark at 1280 and 390 px, no sideways scroll and 44 px targets at 390 px |
| Build | `dotnet build OpenPayments.slnx -warnaserror`: 0 warnings |
| Whole suite | `dotnet test OpenPayments.slnx`: 423 passed, 0 failed. Domain 70, analyzers 1, mock PSP 20, checks 24, integration 40, acceptance 76, console 152, console browser 40 |
| Index use | Each queue read uses its partial index, checked with `EXPLAIN` against the parent index through `pg_inherits` |
| Live stack | Rebuilt the local console container. `/` serves Needs attention with the empty state, and the sidebar shows it first |
| Empty state (owner, 2026-11-09) | The all-clear page now shows only the calm panel and its check time. The title line, freshness line and Refresh button are omitted, matching "the page says so and nothing else". Test: `All_empty_shows_only_one_calm_panel` |
| Empty-state checks | `dotnet build OpenPayments.slnx -warnaserror`: 0 warnings. `dotnet test OpenPayments.slnx`: 432 passed, 0 failed. The local console image was rebuilt and is healthy |

**Decided without the owner,** to confirm:

1. **Unresolved age limit of 15 minutes,** `Console:Attention:UnresolvedAfter`. No document sets one. CK5's 60 seconds is the reconciler's check, which is too short for a person to look. "Over limit" shows past twice that.
2. **A review case that already has an open proposal isn't counted in the badge,** because the one-open-proposal index refuses a second proposal, so nobody can act on it from Needs review. The row says "A resolution is waiting for approval."
3. **Orphan intents use the Audit log's 10-minute limit,** `Console:Audit:IntentLimit`, rather than a second setting.
4. **The badge reads on every page load**, one bounded count query on the replica for Operators and Approvers, none for other roles. A failed read shows no badge.

**Left for N3 and N4:** the full queue lists (a queue over 10 rows points to Payments search for now), the case panel for `?from=` (the link already carries it; the payment page ignores it), htmx refresh with the live region, the many-unresolved line and its PSP status link, and the 812 px first-row check at 390 px.

## Depends on

C3 and C5 (the `REVIEW` resolution path), and Stage 2's design gate, which may add PSP events and the last inquiry to the case panel. N1 and N2 can be built before the review flow, because they only read.
