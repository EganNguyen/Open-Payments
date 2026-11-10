# Stage 2 Design Gate

The approval record for Stage 2, PSP and recovery: what was decided, what the owner must still decide, and what must be true before the build starts.

Status: **Signed 2026-11-10. Build starts from `57e63c2`** · Last updated: 2026-11-10

Stage 2 builds the real PSP adapter, callbacks, the reconciler and its timers, settlement ingestion, the review tooling and one 3D Secure flow, then freezes the API contract. The rules are in [Delivery workflow](../delivery-workflow.md#design-gate), and the tasks are in the [Stage 2 plan](../superpowers/plans/2026-11-10-stage-2-psp-and-recovery.md).

## Where Stage 2 starts

Checked on 2026-11-10 against the repository, not assumed.

| Fact | Evidence |
|---|---|
| Stage 1's review gate closed 2026-11-10, with L7 and L8 carried here | [Stage 1 review](stage-1-review.md), owner sign-off row |
| D56's laptop verification ran two 60-minute soaks on 2026-11-10 (step 102 at 250 req/s, from 07:45 and 08:47 UTC). Its results are in [BASELINE](../../benchmarks/BASELINE.md) and [Phase 1](../phase-gates/phase-1.md#d56-laptop-verification), not repeated here | `benchmarks/BASELINE.md` |
| No capacity number is proven. The latest valid laptop preview is step 5, 248 completed payments/s, p99 17.74 ms; step 6 failed p99 with one API instance | [BASELINE](../../benchmarks/BASELINE.md) |
| No callback route, reconciler, timer job, breaker, PSP rate limiter or settlement ingestion exists in `src/` | `grep` of `src/` |
| `psp_events` has no link to a payment, and nothing writes it | `db/migrations/0001_initial.sql` |
| **`payments.authorized_until` is never written** by the payment path, so an expiry timer would find nothing | Only `OpenPayments.Console` reads it |
| Off-request key resolution (`PaymentStore.StoreResponseAsync`) finds the key by `attempt_id` or `refund_id`, which no index covers. On a retained table it scans the merchant's partition | `PaymentStore.cs:850-866`; migration `0001` |
| The mock PSP's callbacks are unsigned, carry no event ID, use one global URL, and are retried 3 times only | `tools/OpenPayments.MockPsp/Program.cs` |
| The mock adapter declares `authoritative_lookup` and sale | `MockPspAdapter.cs:32` |

## Needs the owner

**Answered 2026-11-10:** the owner approved every recommendation below in chat, recorded by Claude. O9 names `57e63c2`, the commit that added this record and the plan. The two sandbox accounts and their keys in O5 are still to be placed in `deploy/compose/secrets/` by the owner; Milestone I and G2 wait for them, and no builder creates an account or handles a secret.

**Nothing below a task depends on may stay open when the gate closes.** Each has a recommendation.

| # | Decision | Recommendation | Blocks |
|---|---|---|---|
| O1 | **Which PSP sandbox** | **Stripe**, in sandbox (test) mode. It has sale (automatic capture) and manual capture, a signed webhook per endpoint, 3D Secure test methods that are tokens, never card numbers, and documented idempotency. Adyen's test accounts need approval, and its rate limit and lookup were never confirmed | Tasks 32-35, C39, G2 |
| O2 | **One sandbox account or two** | **Two**, as two merchants. One account can't show per-account routing, the per-account breaker (C51) or G2 per account | Tasks 23, 25, 36; C51 |
| O3 | **The adapter's declared recovery mode** | **Situational, under a new [D60](../decisions.md#d60):** replay with the same key when we hold no PSP reference, lookup by the PSP's own ID when we do, and cancel to prove an abandoned action. Stripe's Search can lag up to an hour and isn't for read-after-write, so it is never used | Task 27, C44, C45 |
| O4 | **Approve D59-D63 as written** (below) | Approve | The whole build |
| O5 | **Who provides the Stripe sandbox keys and webhook secrets** | The owner creates two sandboxes and puts their keys in the local secret store (`deploy/compose/secrets/`, git-ignored). No agent creates accounts or types secrets ([Global Constraints](../superpowers/plans/2026-11-10-stage-2-psp-and-recovery.md#global-constraints)) | Tasks 32-36 |
| O6 | **Timer values decided here** (table below) | Approve | Tasks 27-30 |
| O7 | **G2 in writing:** who asks Stripe for the live per-account limit, and whether a written answer is required before Stage 4 or before live money | Ask before Stage 4; Stage 2 records the sandbox limit and the published live default only | Task 36 |
| O8 | ~~The key-expiry method, once V7 has run~~ **Answered 2026-11-10:** no measurement at the design gate ([D64](../decisions.md#d64)); the method is [decided by analysis](#key-expiry-decided-by-analysis) | Approve the method when signing | Task 30 |
| O9 | **The build-start commit,** and signing this record | The commit that adds this record and the plan, after O1-O8 | Everything |

**If O1 goes another way,** only Milestone I's adapter, its fixtures and C39 change. The mock covers all three recovery modes, so Milestones E-H don't depend on the choice.

## Decisions for the owner to approve (O4)

These are added to [Decisions](../decisions.md) as `Proposed`, and become `Committed` when this record is signed.

| Record | Decision | Cost if wrong |
|---|---|---|
| [D59](../decisions.md#d59) | **A PSP answer is never dropped (L7).** When transaction B loses its conditional update, the store re-reads and runs `Decide` once more. If it is refused again, the answer is written to `psp_events` as a parked `api_answer` event in its own transaction, and the client gets 202 with the current state. If even that write fails, the answer is 202 and recovery finds the answer at the PSP. Callbacks use the same table, apply in one transaction with the insert ([D30](../decisions.md#d30)), and park when the operation can't take the event yet | A parked row per lost race; the worker must exist before callbacks ship |
| [D60](../decisions.md#d60) | **An adapter declares recovery per situation**, not one mode: with no PSP reference, its mode for "never answered" (replay, lookup or cancel); with a reference, lookup by the PSP's ID where the PSP documents it as consistent; to prove an abandoned action, a confirmed cancel. Each is in `PspCapabilities`, and U5 tests each against recorded fixtures | Amends D27's "one mode per adapter". More capability fields |
| [D61](../decisions.md#d61) | **Resolving an operation resolves its key, in the store, in the same transaction.** `payment_attempts` and `refunds` carry the key that started them, so the key is updated by primary key. `ApplyInTransactionAsync` resolves the key whenever an attempt or refund reaches `SUCCEEDED` or `FAILED`, whoever applies it: API, callback, reconciler, timer or manual resolution. The answer is rendered by one renderer moved from the API into `OpenPayments.Transitions`. The authorize half of a two-call auto-capture doesn't resolve its key | A key can stay unresolved if its capture never starts. It is then never expired, which is the safe direction ([E3](stage-1-escalations.md#e3-task-16-idempotency-key-retention-has-no-implementation)) |
| [D62](../decisions.md#d62) | **The reconciler claims work by lease, not by lock.** A short transaction selects due rows with `for update skip locked`, sets `next_recovery_at = now() + lease` and commits. The PSP call happens outside any transaction, and the answer applies through the normal conditional update, so two reconcilers can't both act on a row, and if a lease lapses the same PSP key makes a second call harmless. Recovery spends a per-account token bucket, a fixed share of that account's limit ([D34](../decisions.md#d34)). Timers and the parked-event worker run in the same process | A lapsed lease can call the PSP twice for one key, which the PSP dedupes |
| [D63](../decisions.md#d63) | **Settlement is ingested per account, per file, resumably.** A `settlement_files` row tracks each file. Lines insert idempotently, then match by PSP reference. A matched line posts through `OpenPayments.Transitions` with a ledger ID derived from `(psp_account_id, file_id, line_no)` ([D32](../decisions.md#d32)), so a rerun can't post twice. Discrepancies are the line states `UNMATCHED` and `MISMATCH`, plus `settlement_missing` rows for operations with no line past the adapter's settlement lag. Fee and adjustment lines are stored and post nothing, as [D18](../decisions.md#d18) leaves them `Open` | A new table and a missing-line scan |

## Settled by this gate, without the owner

Each needs the owner's confirmation when signing.

### Callbacks

| Question | Answer |
|---|---|
| Route | `POST /psp/v1/accounts/{psp_account_id}/events` on the Payments API, one URL per PSP account ([architecture](../architecture.md#psp-integration)). It is PSP-facing, so it isn't in `api/openapi.yaml`, like the console's internal route |
| Limits | Body at most 64 KB; its own concurrency limit (D34), starting at 100 per instance; over the limit it answers 503 with `Retry-After`, which PSPs retry. No merchant API key; the signature is the authentication |
| Signature | Verified on the raw body before any write, with the account's signing secret read from the secret store through `psp_accounts.callback_secret_ref`. Timestamp tolerance 5 minutes. A bad signature, an unknown or disabled account answers 401 or 404 and writes nothing |
| Identity and dedupe | `(psp_account_id, psp_event_id)`, the PSP's own event ID. `ON CONFLICT DO NOTHING`; a duplicate answers 200 and changes nothing |
| Finding the operation | Our `attempt_id` or `refund_id`, which the adapter sends to the PSP as its reference ([D30](../decisions.md#d30)), then `psp_reference` |
| The transaction that applies it | One `TxRunner` transaction: insert the event, load the snapshot, `Decide`, conditional update, ledger, outbox, key resolution (D61), set `processed_at` and `outcome`. 2xx only after `COMMIT` |
| Outcomes | `applied`; `ignored` (already in that state, or stale); `contradiction` (contradicts a final state, such as E9: logged, counted, FS-05 alert); `parked` (the operation can't take it yet, such as E10, or the payment's row is locked past `lock_timeout`) |
| Parked events | Retried by the worker with backoff 1, 2, 4 ... 60 s. Past the parked limit, 15 minutes, the worker marks it `expired`, raises an alert, and leaves the operation to the reconciler. CK5 counts parked events past the limit |
| Ordering | None assumed. Stripe [doesn't guarantee order](https://docs.stripe.com/webhooks). Convergence comes from conditional transitions, and C9 tests every permutation |
| Retention | Processed events are kept 7 days, longer than Stripe's redelivery window of up to 3 days, then deleted in batches by the same job as keys. `psp_events` becomes hash-partitioned 16 ways by `psp_event_id`, so the primary key keeps the partition key |
| Link to a payment (F2) | `psp_events` gains `payment_id`, `attempt_id`, `refund_id`, `source`, `outcome`, `attempts`, `next_attempt_at`, `last_error`, with an index on `payment_id` |

### Reconciler and timers

| Question | Answer |
|---|---|
| Process | A new deployable, `OpenPayments.Reconciler`, a .NET worker. Timer jobs and the parked-event worker run in it ([architecture](../architecture.md#components-and-boundaries)) |
| What it picks up | Attempts and refunds `PENDING` or `UNKNOWN` older than the attempt timeout, through `payment_attempts_unresolved` and `refunds_unresolved`, whose `next_recovery_at` has passed |
| Batch and rate cap | 100 rows per claim, a tick every 2 s, and a per-account recovery token bucket at 10% of `rate_limit_per_s` (at least 1/s). The API's live bucket gets the other 90%. Each instance's share is the account limit divided by the instance count |
| Stored inquiry (F2) | `last_inquiry_at`, `last_inquiry_result` and `recovery_attempts` on `payment_attempts` and `refunds` |
| Concurrency | D62 leases; every apply is a conditional update. C26 runs two reconcilers at once |
| Replay window | Replay only while `now < created_at + replay_window`, with `replay_window` at most a quarter of key retention. Starts at 1 hour for 24 h retention. After it, no replay is ever sent (C45); the attempt waits for a callback or the age limit |
| Coexistence with expiry and late voids | Expiry runs only with no live capture or void, which `Decide` already refuses (D37). A late void has its own attempt row, key and index, so it can't block a capture. The reconciler issues late voids; it never starts an authorize |

| Timer | Value | Order check ([D34](../decisions.md#d34)) |
|---|---|---|
| PSP deadline | 2.5 s | > database call 200 ms |
| Request budget | 5 s | > PSP deadline |
| Attempt timeout | 30 s | > request budget |
| Replay window | 1 h, at most retention ÷ 4 | > attempt timeout; well inside retention |
| Escalation age limit ([D45](../decisions.md#d45)) | 2 h | > replay window, so recovery has finished trying |
| Parked event limit | 15 min | Below the escalation age limit |
| Action window | 15 min | From the [lifecycle](../payment-lifecycle.md#timers) |
| Payment window | 24 h | From the lifecycle |
| Authorization window | The adapter's declared value, set 1 day short of the PSP's (Stripe card: 7 days, so 6) | Rechecked at adapter time |
| Key retention | 24 h after `resolved_at`; startup refuses a lower value | [D19](../decisions.md#d19) |
| PSP key retention | Stripe at least 24 h ([source](https://docs.stripe.com/api/idempotent_requests)) | > replay window × 4 |

**`resolved_at` on every resolving path:** D61 makes it one code path, and Task 20's test drives every command that can finish an attempt or refund (PSP answer, recovery answer, callback, void answer, refund confirm or release, manual resolution, late approval reversed) and asserts the key resolved in the same commit. Escalation to `REVIEW` and timeouts don't resolve a key.

**`authorized_until`** is written in transaction B whenever a payment becomes `AUTHORIZED`, from the attempt's time plus the adapter's authorization window. Task 28 tests it.

### Other

| Question | Answer |
|---|---|
| L8 | Fixed before any adapter without sale, even though Stripe has sale, because the mock can turn sale off and D47 needs that run. Task 22 |
| Breakers and limits | A breaker per PSP and one per account (C51); a token bucket per account; 429 from the PSP backs off by `Retry-After` and counts against the bucket, with no retry under a new key (C12). With one PSP, C13's failover is built and tested between two mock PSPs only |
| Connections | `SocketsHttpHandler.PooledConnectionLifetime` 1 minute, so DNS changes are seen; a connect failure retries, because nothing was sent (C16) |
| 3D Secure | Stripe's 3D Secure test payment methods, which are tokens. The action is completed or failed by Playwright on Stripe's test page; "abandoned" lets a 2-minute test action window lapse |
| API freeze | After C39 passes, `api/openapi.yaml` becomes version 1.0.0, its title loses "Draft", and any later change needs the owner and a decision record ([D17](../decisions.md#d17)) |
| Settlement source for Stripe | The itemized payout reconciliation report, or balance transactions by payout. **Unverified in sandbox:** Stripe's Reports API guide says report runs need a live key, while its reconciliation examples use test keys. Task 31 builds against the mock; Task 32 checks the sandbox, and if reports aren't available there, settlement is proven against the mock only and that is named in the Phase 2 record |
| Console (F2) | Console Task C3 lands before Task 29's escalation job; then C5 and C12 with N3 and N4; the timeline adds PSP events and the last inquiry |

## Key expiry, decided by analysis

The owner decided on 2026-11-10 that design gates analyze and design, and run a measurement only when a slowness has been seen and is to be optimized ([D64](../decisions.md#d64)). So V7 isn't run here. A `V7-control` run was started on the native stack and stopped before it measured anything; it left no result. This answers [E3](stage-1-escalations.md#e3-task-16-idempotency-key-retention-has-no-implementation)'s first check, and amends [D46](../decisions.md#d46)'s requirement to measure V7 at this gate.

**The method,** from the queries, the plans and how PostgreSQL deletes:

| Choice | Reason |
|---|---|
| **Delete partition by partition,** looping over the 16 `idempotency_keys` partitions, not through the parent | A `limit` over the parent appends partitions in turn and may scan one partition's index while another holds the oldest keys. A statement per partition uses that partition's own `resolved_at` index and touches one partition's pages |
| **One batch:** `delete from idempotency_keys_<n> where (merchant_id, key) in (select merchant_id, key from idempotency_keys_<n> where resolved_at < $cutoff order by resolved_at limit $batch for update skip locked)` | `resolved_at < $cutoff` can't match a null, so an unresolved key is never deleted, by the statement itself. `order by resolved_at` walks the partial index from its oldest end. `skip locked` never waits on a live writer |
| **Batch 500, each its own transaction,** well inside the role's 1 s `statement_timeout` | Short transactions keep lock time and WAL bursts small, and a failed batch loses nothing |
| **Rate cap** `Expiry:MaxDeletesPerSecond`, starting at twice the expected key creation rate, with a pause between batches | Expiry competes with payments for WAL and disk. Twice creation drains any backlog while staying bounded |
| **Dead index entries at the oldest end** are skipped through PostgreSQL marking them dead on first visit, and cleared by autovacuum. Migration `0004` sets `autovacuum_vacuum_scale_factor = 0.01` and `autovacuum_vacuum_insert_scale_factor = 0.01` on each `idempotency_keys` partition | Deleting in `resolved_at` order is the case where dead entries collect at the head of the index. The default 20% scale factor would wait for tens of millions of dead rows per partition at target |
| **Watch, then optimize:** metrics `op_keys_expirable_backlog`, `op_keys_expired_total`, `op_expiry_batch_seconds`, and an alert `KeyExpiryFallingBehind` when the oldest expirable key is more than retention plus 1 hour old | This is the signal that would justify measuring. If it fires, or if the stage benchmark shows live p99 rising while expiry runs, V7 is run then, to find the cause |

`psp_events` uses the same method on its processed rows, partition by partition on `psp_events_expirable`.

**Not established:** the cost of expiry on real disks at 10,000 keys/s. The stage benchmark (Task 36) runs with expiry on and reports the backlog; a Linux run comes before Stage 4.

## Must finish before the build

| ☐ | Item | Needs |
|---|---|---|
| ☑ | O1-O7 and O9 answered | Owner, in chat 2026-11-10 |
| ☑ | Key-expiry method recorded | [By analysis](#key-expiry-decided-by-analysis), under D64 |
| ☑ | D59-D63 `Committed` in [Decisions](../decisions.md) | Owner's approval, 2026-11-10 |
| ☑ | [Stage 2 plan](../superpowers/plans/2026-11-10-stage-2-psp-and-recovery.md) written | This gate |
| ☑ | Signed below, naming the build's starting commit | Owner |

## Risks carried into the build

- **Every Stage 2 run is a preview.** The laptop can't answer C23 "at 5k TPS", and steps 4-5 at Linux scale remain blocked by [E4](stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4). Rows are ticked only from recorded runs, from Phase 3 on.
- **Stripe's sandbox limit is 25 requests/s per account**, against 100 in live mode ([source](https://docs.stripe.com/rate-limits)), and Stripe discourages load testing in sandbox. So the sandbox proves behavior, never capacity, and benchmarks keep the mock.
- **G2 is a stop condition.** At 100 requests/s per account, 5,000 payments/s needs at least 50 busy merchant accounts at once, or a raised limit. That fits D35, but it is arithmetic, not a written answer.
- **Settlement against the sandbox may be impossible** (above).
- **C13 failover never triggers with one real PSP,** because tokens don't move between PSPs ([D39](../decisions.md#d39)).
- **Key expiry's cost is unmeasured.** The method is chosen by analysis, and `KeyExpiryFallingBehind` and the stage benchmark are what would show it's too slow.
- **D56 is unfinished.** If it finds a defect in the payment path, its fix comes before Stage 2's build.
- **The API contract may change at C39,** so the console's API client and the harness may need updates after the freeze.

## Sign-off

The owner fills this in when the gate closes.

| Approved by | Date | Build starts from commit |
|---|---|---|
| EganNguyen, in chat, recorded by Claude | 2026-11-10 | `57e63c2` |
