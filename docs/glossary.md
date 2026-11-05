# Glossary

Plain-language definitions of the terms used across the Open Payments docs.

Status: Draft · Last updated: 2026-11-05

## Terms

| Term | Meaning |
|---|---|
| **Admission control / backpressure** | When overloaded, the system says "busy" quickly instead of letting requests pile up. |
| **Append-only** | Records are never edited or deleted. A mistake is fixed by adding a correcting record. |
| **Cell** | A complete, independent copy of the stack serving some merchants. More cells add capacity and limit the blast radius of a failure. |
| **Double-entry ledger** | Every movement of money is written as matching debit and credit lines. If totals differ, something is wrong. |
| **Idempotency** | Repeating a request has the same effect as doing it once, like pressing an elevator button twice. |
| **Little's Law** | Requests in progress = requests per second × seconds each takes. |
| **Outbox** | The note "tell other systems about this payment" is saved in the same database transaction as the payment, then delivered later. Either both exist or neither does. |
| **p95 / p99** | 95 or 99 of every 100 requests are faster than this time. It shows the slow cases averages hide. |
| **RPO / RTO** | How much committed data can be lost, and how long recovery may take. |
| **Reconciliation** | A background job asks the PSP what really happened and records the answer, and compares PSP settlement files with the ledger. |
| **Runbook / drill** | Ordered recovery steps, and a rehearsal of them under an injected fault. |
| **State machine** | A payment can move only along allowed steps. Anything else is rejected. |
| **Unknown state** | The PSP outcome is not known yet. This is a real state, not a failure. |
| **Vault / tokenization** | Card numbers stay in a separate locked service, and the rest of the system sees only a token. |

## Standard names

| Use | Meaning | Not |
|---|---|---|
| **completed payments/s** | Payments reaching a final committed state. This is the 5,000 goal. | "5,000 requests" |
| **req/s** | HTTP load offered to the system | Goal throughput |
| **TPS** | Only as shorthand for completed payments/s | HTTP load |
| **Payments API** | The service running the payment path | "Payment service" |
| **outbox publisher** | The worker moving committed outbox events to the broker | "relay" |
| **PSP** | Payment service provider: the company or bank connection that moves card money | "bank", when the PSP is meant |
| **`Committed` / `Proposed` / `Open`** | Part of the design / suggested but not adopted / not yet decided | Any other status label |
