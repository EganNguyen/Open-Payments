# Open Payments: project overview

A plain-language guide to what this project is, how it works, and where everything lives. Read this first, then the other documents.

Status: draft · Date: 2026-11-04 · No code exists yet. This is a design-stage project.

## 1. What this project is

Open Payments is a plan for a **payment processing system**: software that lets a business (a "merchant") charge a customer's card, refund it, and see what happened, much like Stripe does.

The aim is to build it with open-source parts and C# (.NET), and to prove that it can handle **5,000 payments per second** without ever charging a customer twice or losing a record of money.

There are four parts of work so far:

| Part | What it is |
|---|---|
| The design | How the system is built and why it is correct (`payment-system-design.md`) |
| The scaling plan | How to prove it handles 1 to 5,000 requests per second (`scaling-playbook.md`) |
| The failure plan | What can go wrong, what the system does, and how people recover (`failure-scenarios.md`) |
| The UX prototype | Clickable screens for the merchant dashboard and the shopper's checkout, plus a case study (built earlier as a design canvas, for your UX final project) |

## 2. The goal in numbers

| Goal | Meaning |
|---|---|
| 5,000 payments per second | "TPS" means transactions per second. Sustained, not just a short burst |
| Up to 15,000 per second in bursts | Three times the target, so a spike or a lost server does not break it |
| Zero duplicate charges | A customer is never charged twice for one purchase |
| Zero lost records | Every committed payment is stored and can be found |
| Always-balanced books | Money in always equals money out in the accounting records |
| Platform delay p99 ≤ 100 ms | For 99 of every 100 requests, our own processing takes 100 ms or less (not counting the bank's time) |

These are **targets**. Nothing has been measured yet.

## 3. What happens when someone pays

```
Customer pays $24
   |
   v
1. API receives the request and checks it is allowed
2. Fraud check (quick, must finish in about 50 ms)
3. Save the payment and an "attempt" in the database   <- written BEFORE asking the bank
4. Ask the payment provider (PSP / bank) to charge the card
5. Save the result, the accounting entries and a "tell others" note
   in ONE database transaction
6. Answer the customer: success, declined, or "still checking"
7. Later, in the background: tell the merchant (webhook), update balances
```

**What if something goes wrong?**

- **The customer clicks twice or the network retries.** The system recognises the same request (by its idempotency key) and returns the first answer. No second charge.
- **The bank approves but our server crashes before saving.** Step 3 saved the attempt first. A background job (the reconciler) asks the bank "what happened to attempt X?" and records the true answer.
- **The bank does not answer in time.** The payment is marked **unknown**, not failed. The reconciler finds out later.
- **The message to the merchant cannot be sent.** It waits safely in the outbox and is sent when the system recovers.

## 4. Key ideas explained simply

| Term | Plain explanation |
|---|---|
| **Idempotency** | Doing the same request twice has the same effect as doing it once, like pressing an elevator button twice. |
| **State machine** | A payment can only move along allowed steps (created, pending, authorized, captured, refunded). Anything else is rejected. |
| **Ledger / double-entry** | Every movement of money is written as two matching lines, one debit and one credit. If the totals do not match, something is wrong. |
| **Append-only** | Records are never edited or deleted. A mistake is fixed by adding a new correcting record. |
| **Outbox** | The note "tell other systems about this payment" is saved in the same database transaction as the payment, then delivered later. Either both exist or neither does. |
| **Unknown state and reconciliation** | When the outcome is not known, the system keeps the payment in a special state and a background job resolves it by asking the bank. |
| **PSP** | Payment service provider: the company or bank connection that actually moves the card money. |
| **Vault / tokenization** | Card numbers are kept in a separate locked service. The rest of the system only sees a token, which keeps security audits small. |
| **Cell** | A complete, independent copy of the system that serves some merchants. More cells means more capacity and a smaller blast radius when one fails. |
| **Backpressure / admission control** | When the system is overloaded it says "busy" quickly instead of letting requests pile up until everything breaks. |
| **p95 / p99** | 95 or 99 of every 100 requests are faster than this time. It shows the slow cases that averages hide. |
| **Little's Law** | Requests in progress = requests per second × seconds each takes. At 5,000 per second and 1 second per bank call, about 5,000 requests wait at once. |

## 5. The pieces of the system

| Piece | Job |
|---|---|
| Payments API | Receives payments and refunds, enforces idempotency, writes the database and ledger |
| PSP adapters | Talk to banks and providers with time limits, retries that are safe, and failover |
| Vault | Stores card data separately |
| Fraud check | Quick rules before charging |
| Database (PostgreSQL) | The source of truth for payments and the ledger |
| Outbox publisher and broker (Kafka) | Carry events to everything that does not need to be in the payment request |
| Ledger projector | Builds account balances from the ledger entries |
| Webhook dispatcher | Tells merchants what happened, with retries |
| Reconciler | Fixes unknown payments and compares against the bank's records |

Build order: payments core, idempotency, ledger, one PSP adapter, outbox plus broker, reconciler. Settlement, payouts and marketplace features come later.

## 6. Why 5,000 per second is hard

- Money must be **exactly right** even when servers crash halfway.
- Many payments touch the **same accounts**, which can make them wait on each other.
- Banks are **slow and sometimes fail**, and we cannot control them.
- At this volume, a rare slowdown affects **dozens of payments every second**.
- Something is **always** partly broken, so the question is how the system behaves when it is.

The answer in this design: save intent first, make everything safe to repeat, treat "unknown" as a real state, bound every queue and timeout, and prove each claim with a test.

## 7. Document map

| File | What it is | Read when |
|---|---|---|
| `README.md` | This overview | First |
| `payment-system-design.md` | Full design, the test catalogue, and a 108-row checklist tying every known production problem to a fix and a test | When you want to know how and why |
| `scaling-playbook.md` | The step-by-step plan to prove 1 to 5,000 requests per second, gates, sizing, and a results log template | When you plan the benchmarks |
| `failure-scenarios.md` | 41 failure scenarios, 17 edge cases, 7 recovery runbooks and a drill plan, each tied to a test | When you plan for things going wrong |
| UX prototype (design canvas) | Clickable merchant dashboard and checkout, plus a UX case study | For the UX project |

## 8. Where the project stands

**Done**

- Architecture and design written, including data model, flows and failure handling.
- 108 production problems mapped to a fix and a test (none verified yet: **0 of 108**).
- Scaling plan with ramp steps, decision gates and a results template.
- Failure scenarios, edge cases, recovery runbooks and drills (none rehearsed yet).
- UX prototype and case-study template.

**Not done**

- No code, no benchmarks, no real measurements.
- Not designed yet: settlement and payouts, marketplace (Connect) features, billing, advanced fraud, and the formal card-industry (PCI) assessment.

**Next steps (suggested)**

1. A 2-3 day database spike: measure how many payment transactions one PostgreSQL primary sustains. This decides whether one cell is enough.
2. Write three short documents: API contract, state machine and event catalog, and decision records.
3. Write about 10 acceptance tests that fail first.
4. Phase 0: mock PSP, load scripts, automatic correctness checks. Then the payments core.

## 9. Suggested reading order for a newcomer

1. This file.
2. Design doc sections 1 to 3 (goals, principles, architecture).
3. Design doc sections 6 and 7 (payment lifecycle and the payment flow).
4. Design doc sections 8 and 9 (data model and ledger).
5. Scaling playbook sections 1 to 3 (process and ramp plan).
6. Skim the checklist in design doc section 19 to see how problems map to fixes.

## 10. Honest limits

- All numbers (latency, capacity, cost, effort) are estimates or targets until measured.
- A payment platform also needs legal, banking and card-industry compliance work that this design does not cover.
- A mock bank is not a real bank. Real provider testing is required before trusting any result.
