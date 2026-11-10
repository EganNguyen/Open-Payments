# Service Catalog

What Open Payments deploys today, how those parts depend on each other, and where every platform capability would live.

Status: Draft · Last updated: 2026-11-05

## How to read this catalog

This doc keeps three things apart:

1. **[Open Payments today](#open-payments-today):** what this design commits to, with deliberately few deployable services.
2. **[Capability map](#capability-map):** a proposed map of what a full payment platform needs. It's our own inference from business capabilities, and it is **not Stripe's internal architecture**.
3. **[Public Stripe reference](#public-stripe-reference):** only what Stripe has published, with sources.

A capability is not a service. The map has 44 capabilities, but only 8 deployable services exist in the design. Nothing is built yet.

| Kind | Meaning |
|---|---|
| Deployable | Runs and scales as its own process |
| Module | Code inside a deployable, sharing its transaction |
| Library | Code linked into one or more deployables |
| Infrastructure | A data store or platform component we run or buy, not our service code |
| Planned | A future deployable that is not designed yet |
| External | Run by someone else |

## Open Payments today

Eight deployables, two libraries and four infrastructure parts. Our own vault is deferred ([D39](decisions.md#d39)). How they fit together is in [Components and boundaries](architecture.md#components-and-boundaries).

| Component | Kind | Boundary | Job | Owns | Customer waits? | If it is down |
|---|---|---|---|---|---|---|
| Edge gateway | Deployable | Shared | TLS, API key and token authentication, per-merchant rate limits, size and slow-client limits, fast 429 or 503 with `Retry-After` | nothing | Yes | Nothing can enter; scale out |
| Cell router | Deployable | Shared | Maps merchant and API key mode to a cell from a cached table; no payment logic | merchant-to-cell table | Yes | Uses its last known mapping |
| Payments API | Deployable | Cell | Create, new attempt, cancel, capture, refund, status query, idempotency, PSP callback receiver | idempotency keys | Yes | Payments stop; other cells keep working |
| Fraud decision | Deployable | Cell | Rules within about 50 ms, Redis velocity counters, fallback by risk tier, async analytics | rules, counters | Yes, with a time limit | Fallback policy applies |
| Outbox publisher | Deployable | Cell | Moves committed events to the broker in batches | nothing | No | Events wait in the outbox |
| Ledger projector | Deployable | Cell | Builds balances and snapshots from entries; rebuildable read model | balance snapshots | No | Balances lag; payments unaffected |
| Webhook dispatcher | Deployable | Cell | Signed delivery, retries with backoff, dead-letter queue, per-endpoint isolation | delivery log | No | Notifications are late, never lost |
| Reconciler | Deployable | Cell | Resolves unknown and stuck operations, runs timer jobs, ingests and matches PSP settlement files | discrepancy records | No | Unresolved work stays unresolved longer |
| Payment-transition module | Library | Linked into the Payments API and reconciler | State machine, atomic refund limit, ledger and outbox writes; the only code that writes money state | payments, attempts, refunds, ledger, outbox | Yes | Fails with its host process |
| PSP adapters | Library | Linked into the Payments API and reconciler | One adapter per PSP behind one interface; deadline, breaker, rate limit, recovery mode, failover where allowed. A separate bulkhead per PSP | adapter metadata | Yes | Payment becomes `UNKNOWN` or fails fast |
| PostgreSQL | Infrastructure | Cell | Stores all money state, with synchronous standbys | all financial data | Yes | Standby takes over; committed payments are kept |
| PgBouncer | Infrastructure | Cell | Pools database connections | nothing | Yes | Writes stop until it is replaced |
| Kafka or Redpanda | Infrastructure | Cell | Carries events to workers | event log | No | Events wait in the outbox |
| Redis | Infrastructure | Cell | Fraud counters and fast non-authoritative checks | counters | Only for fraud checks | Rate limits fall back to in-process; idempotency unaffected |
| PSPs and banks | External | Outside | Move the card money through each merchant's own account ([D35](decisions.md#d35)), and host the card fields | their records, card data | Yes | Outside our control, hence `UNKNOWN` |
| Merchant webhook endpoints | External | Outside | Receive notifications | their records | No | Retry, then dead-letter |

Observability and the Kubernetes platform support all of these. The mock PSP and load harness are test tools, built first.

The staff console (`src/OpenPayments.Console`) is a ninth deployable, added by the [console spec](superpowers/specs/2026-11-05-console-design.md) after this catalog was written. It reads the database through read-only roles, signs staff in through an OIDC provider, and moves money only by calling the Payments API. It is not on the payment path: if it is down, payments are unaffected.

## Dependencies

The [communication diagram](architecture.md#how-services-communicate) draws these lines.

| From | To | Type | Purpose | If the dependency fails |
|---|---|---|---|---|
| Edge gateway | Cell router | Sync | Find the merchant's cell | Last known mapping |
| Cell router | Payments API | Sync | Forward the request | Payments in that cell stop |
| Payments API | Fraud decision | Sync | Check within about 50 ms | Fallback by risk tier |
| Fraud decision | Redis | Sync | Velocity counters | In-process limits, per the fallback policy |
| Payments API | PSP adapters (in-process) | Sync, then PSP over the network | Authorize, capture, refund | `UNKNOWN`, or fast failure |
| Payments API, reconciler | Payment-transition module (in-process) | Sync | Write state, ledger and outbox in one transaction | Operation fails; nothing partial commits |
| Payment-transition module | PostgreSQL through PgBouncer | Sync | Commit money state | Fast failure; standby takes over |
| PSPs | Payments API | Async callback | Report outcomes | Reconciler inquires |
| Reconciler | PSP adapters (in-process) | Sync within the worker | Inquire about unresolved operations | Stays unresolved; alert past the age limit |
| Outbox publisher | PostgreSQL, broker | Async | Publish committed events | Events wait in the outbox |
| Ledger projector | Broker | Async | Build balances | Balances lag |
| Webhook dispatcher | Broker, merchant endpoints | Async | Deliver signed events | Retry, then dead-letter |

Event names are in [Merchant events](payment-lifecycle.md#merchant-events). A full event catalog is still `Open`.

## When to split a service

A capability becomes its own deployable only if **all** of these are true ([D15](decisions.md#d15)):

1. It shares no transaction with money state.
2. It needs its own scaling, failure or compliance boundary.
3. It has its own owner.
4. If it's on the payment path, the extra network hop has been measured against the [100 ms target](architecture.md#goals-and-slos).

Throughput alone doesn't justify a split. Authorization, capture, refund, the idempotency check, ledger writes and outbox writes stay together, because they change one payment state machine in one transaction. A network boundary between them would bring back "payment succeeded but ledger failed".

**Planned deployables,** added only when their capability enters scope:

| Planned deployable | Capabilities | Likely boundary |
|---|---|---|
| Merchant Platform | Merchants, customers, connected accounts, configuration | Shared |
| Money Movement | Transfers, payouts, settlement | To decide when designed |
| Billing | Billing, subscriptions, invoices, tax integration | Shared |
| Identity and Compliance | Identity, KYC, AML, compliance controls | Shared |

## Capability map

Our own proposal, not Stripe's internal architecture. **Money** means the capability writes money state, which it may do only through the payment-transition module. **Status** uses the [standard words](glossary.md#standard-names); `Open` means not designed. Build-or-buy entries for `Open` rows are starting suggestions.

### Payments

| # | Capability | Status | Kind | Home | Money | Build or buy |
|---|---|---|---|---|---|---|
| 1 | Payment lifecycle | `Committed` | Module | Payments API | Yes | Build |
| 2 | Authorization | `Committed` | Module | Payments API | Yes | Build |
| 3 | Capture | `Committed` | Module | Payments API | Yes | Build |
| 4 | Refund | `Committed` | Module | Payments API | Yes | Build |
| 5 | Void | `Committed` | Module | Payments API | Yes | Build |
| 6 | Payment methods | `Committed` | Module | Payments API with PSP tokens, cards only; wallets and bank methods are `Open` | No | Build |
| 7 | PSP connectors | `Committed` | Library | PSP adapters | No | Build |
| 8 | Payment routing | `Committed` | Library | PSP adapters, failover only; routing strategy is `Open` | No | Build |
| 9 | Payment recovery | `Committed` | Module | Reconciler | Yes | Build |

### Financial core

| # | Capability | Status | Kind | Home | Money | Build or buy |
|---|---|---|---|---|---|---|
| 10 | Ledger writes | `Committed` | Module | Payment-transition module | Yes | Build |
| 11 | Balances | `Committed` | Deployable | Ledger projector | No, read model | Build |
| 12 | Financial accounts | `Committed` ([D21](decisions.md#d21)) | Module | Payments API | Yes | Build |
| 13 | Transfers | `Open` | Planned | Money Movement | Yes | Build |
| 14 | Payouts | `Open` | Planned | Money Movement | Yes | Build |
| 15 | Settlement | `Proposed`; file ingestion is `Committed` ([D18](decisions.md#d18)) | Planned; ingestion is in the reconciler | Money Movement | Yes | Build |
| 16 | Reconciliation | `Committed` | Deployable | Reconciler | Yes | Build |
| 17 | Fees | `Open` | Module | Payments API, posted in the payment transaction | Yes | Build |
| 18 | Currency rules | `Committed` | Module | Reference table used by the Payments API; FX is `Open` | No | Build |

### Risk and compliance

| # | Capability | Status | Kind | Home | Money | Build or buy |
|---|---|---|---|---|---|---|
| 19 | Fraud detection | `Committed` | Deployable | Fraud decision | No | Build; machine learning is `Open` |
| 20 | Risk tiers and rules | `Committed` | Module | Fraud decision | No | Build |
| 21 | Identity verification | `Open` | Planned | Identity and Compliance | No | Likely buy |
| 22 | KYC | `Open` | Planned | Identity and Compliance | No | Likely buy |
| 23 | AML | `Open` | Planned | Identity and Compliance | No | Likely buy |
| 24 | Disputes | `Proposed` | Module | Payments API | Yes | Build |
| 25 | Compliance controls | `Open` | Planned | Identity and Compliance | No | Mixed |

### Merchant platform

| # | Capability | Status | Kind | Home | Money | Build or buy |
|---|---|---|---|---|---|---|
| 26 | Merchant profiles | `Open` | Planned | Merchant Platform | No | Build |
| 27 | Customer profiles | `Open` | Planned | Merchant Platform | No | Build |
| 28 | Connected accounts | `Open` | Planned | Merchant Platform | No | Build |
| 29 | Billing | `Open` | Planned | Billing | Through payments | Build |
| 30 | Invoices | `Open` | Planned | Billing | No | Build |
| 31 | Tax calculation | `Open` | Planned | Billing | No | Likely buy |
| 32 | Subscriptions | `Open` | Planned | Billing | Through payments | Build |
| 33 | Merchant webhooks | `Committed` | Deployable | Webhook dispatcher | No | Build |
| 34 | Email and other notifications | `Open` | Planned | Merchant Platform | No | Likely buy |

### Platform

| # | Capability | Status | Kind | Home | Money | Build or buy |
|---|---|---|---|---|---|---|
| 35 | API gateway | `Committed` | Deployable | Edge gateway | No | Build on an existing gateway |
| 36 | Idempotency | `Committed` | Module | Payments API, enforced by database constraints | Yes | Build |
| 37 | Event distribution | `Committed` | Deployable | Outbox publisher, with the outbox write in the payment-transition module and the broker as infrastructure | No | Build the publisher; buy or run the broker |
| 38 | Audit records | `Committed` | Module | Append-only audit log in the owning deployable | No | Build |
| 39 | Reporting | `Open` | Planned | To decide; reads from replicas | No | To decide |
| 40 | Analytics | `Open` | Planned | To decide; consumes events | No | To decide |
| 41 | Configuration | `Committed` | Module | Control plane cached in each cell; its owner is `Open` | No | Build |
| 42 | Secrets | `Committed` | Infrastructure | External secrets with rotation | No | Buy |
| 43 | Vault and tokenization | Deferred ([D39](decisions.md#d39)) | Planned | PSP-hosted fields and tokens in v1 | No | Buy from the PSP |
| 44 | Cell routing | `Committed` | Deployable | Cell router | No | Build |

## Public Stripe reference

Checked 2026-11-05. Stripe is a useful reference for product scope, but what it publishes doesn't give a full map of its internal services. Neither engineering post below lists them, and we did not find a public inventory. The [capability map](#capability-map) is therefore not Stripe's confirmed design.

| Published item | What Stripe says it does | Related capability here | Source |
|---|---|---|---|
| Payments (product) | Accepting payments online, in person and internationally | Payments | [stripe.com/payments](https://stripe.com/payments) |
| Connect (product) | Embedding payments for platforms and marketplaces | Connected accounts | [stripe.com/connect](https://stripe.com/connect) |
| Billing (product) | Managing subscriptions, usage-based billing and invoicing | Billing, subscriptions, invoices | [stripe.com/billing](https://stripe.com/billing) |
| Radar (product) | Detecting and blocking fraud | Fraud detection | [stripe.com/radar](https://stripe.com/radar) |
| Tax (product) | Automating sales tax, VAT and GST compliance | Tax calculation | [stripe.com/tax](https://stripe.com/tax) |
| Identity (product) | Verifying users with ID documents and selfie matching | Identity verification, KYC | [stripe.com/identity](https://stripe.com/identity) |
| Idempotency keys (API design post, 2017) | Letting clients safely retry requests so an operation happens only once | Idempotency | [Stripe blog](https://stripe.com/blog/idempotency) |
| Ledger (internal system post, 2024) | An immutable, auditable, double-entry system of record for money movement | Ledger writes | [Stripe developer blog](https://stripe.dev/blog/ledger-stripe-system-for-tracking-and-validating-money-movement) |

A related capability is a comparison, not a claim that Stripe builds it the same way. Stripe's Ledger post, for example, doesn't tell us how its ledger is deployed relative to payments. Open Payments keeps ledger writes in the payment transaction for [its own reasons](decisions.md#d15).
