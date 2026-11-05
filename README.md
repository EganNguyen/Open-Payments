# Open Payments

A design for an open-source payment processing system in C# (.NET) that handles 5,000 completed payments per second without double-charging anyone or losing a record of money.

Status: Draft · Last updated: 2026-11-05

**This is a design-stage project.** There is no code yet, and every number is a target or estimate until measured.

## What it is

Open Payments lets a business (a "merchant") charge a customer's card, refund it, and see what happened, much like Stripe does.

| Goal | Target |
|---|---|
| Throughput | 5,000 completed payments/s, sustained |
| Overload | Survive 15,000 req/s by rejecting the excess quickly, without crashing |
| Duplicate charges | Zero |
| Lost records | Zero |
| Books | Always balanced |
| Our own delay | p99 ≤ 100 ms, not counting the PSP's time |

Full targets are in [Goals and SLOs](docs/architecture.md#goals-and-slos). New terms are explained in the [glossary](docs/glossary.md).

## What happens when someone pays

```
Customer pays $24
   |
   v
1. API receives the request and checks it is allowed
2. Fraud check (must finish in about 50 ms)
3. Save the payment and an "attempt" in the database   <- written BEFORE asking the PSP
4. Ask the PSP to charge the card
5. Save the result, the accounting entries and a "tell others" note
   in ONE database transaction
6. Answer the customer: success, declined (try again), action needed, or "still checking"
7. Later, in the background: tell the merchant (webhook), update balances
```

**If something goes wrong:**

- **The customer clicks twice or the network retries.** The idempotency key identifies the same request, so it returns the first answer with no second charge.
- **The PSP approves but our server crashes before saving.** The saved attempt lets the reconciler ask the PSP what happened and record the true answer.
- **The PSP does not answer in time.** The payment is marked `UNKNOWN`, not failed, and the reconciler finds out later.
- **The message to the merchant cannot be sent.** It waits in the outbox and is sent when the system recovers.

## Why this is hard

Money must be exactly right even when servers crash halfway, PSPs are slow and sometimes fail, and at this volume something is always partly broken. The design answers this by saving intent first, making everything safe to repeat, treating "unknown" as a real state, bounding every queue and timeout, and proving each claim with a test.

## Documents

Read them in this order if you are new.

| Doc | What it covers | Read when |
|---|---|---|
| [Architecture](docs/architecture.md) | Goals, components, payment flow, data model, ledger, PSP integration | First, to learn how and why |
| [Service catalog](docs/service-catalog.md) | Deployable services, dependencies, split rule, full capability map | You need to know what runs where |
| [Payment lifecycle](docs/payment-lifecycle.md) | States, transitions, refund rules, merchant events | You work on payments or refunds |
| [Engineering standards](docs/engineering-standards.md) | Load control, .NET, database, Kubernetes, security, observability | You write or operate code |
| [Deadlock handling](docs/deadlock-handling.md) | Risks, prevention, retry behavior, proposed automation | Before writing database transaction code |
| [Failure scenarios](docs/failure-scenarios.md) | 42 failures, 24 edge cases, 7 runbooks, drills | You plan for things going wrong |
| [Scaling playbook](docs/scaling-playbook.md) | Ramp plan, sizing, decision gates, results log | You plan benchmarks |
| [Verification](docs/verification.md) | Test IDs, 108 production problems, 25 readiness items, build phases | You need to prove a claim or plan a build phase |
| [Decisions](docs/decisions.md) | Why key choices were made, assumptions, open questions | You question a choice |
| [Stripe research](docs/stripe-research.md) | What Stripe has published, proposed design changes, what not to copy | Before accepting the design or starting Phase 0 |
| [Glossary](docs/glossary.md) | Terms and standard names | Any time |

The clickable UX prototype and case study for the merchant dashboard and checkout are kept outside this repo, on a design canvas.

## Where the project stands

**Done (on paper):**

- Architecture, data model, flows and failure handling.
- 108 production problems mapped to a fix and a test, with **0 of 108** verified.
- Scaling plan, failure scenarios, runbooks and drills, with none rehearsed.
- A UX prototype, outside this repo.

**Not done:**

- No code, benchmarks or real measurements.
- Not designed: payouts and settlement beyond file ingestion, marketplace features, billing, advanced fraud, and the formal PCI assessment.

**Suggested next steps:**

1. Run a 2-3 day database spike to measure how many payment transactions one PostgreSQL primary sustains. This answers [gate G1](docs/scaling-playbook.md#decision-gates).
2. Write the API contract and event catalog.
3. Write about 10 acceptance tests that fail first.
4. Start [Phase 0](docs/verification.md#build-phases): mock PSP, load scripts, automatic correctness checks. Then build the payments core.

## Limits

- All numbers are estimates or targets until measured.
- A payment platform also needs legal, banking and card-industry compliance work that this design does not cover.
- A mock PSP is not a real PSP. Real provider testing is required before trusting any result.

More in [Assumptions and limits](docs/decisions.md#assumptions-and-limits).
