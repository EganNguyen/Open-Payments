# Stage 2 Plan: PSP and Recovery

> **For whoever executes this:** Follow [Delivery workflow](../../delivery-workflow.md). DeepSeek builds Tasks 19-37 and console Tasks C3, C5 and C12, test first, in the order given. Opus reviews the complete change since the starting commit in a fresh session that didn't design or build it. Builders stop and escalate on any design conflict, and never decide it themselves. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Connect Open Payments to a real PSP sandbox and make every unresolved outcome resolve safely: callbacks, the reconciler and its timers, settlement ingestion and the review tooling, with one 3D Secure flow that lets the API contract freeze.

**Architecture:** The Payments API gains a callback route per PSP account that inserts and applies each event in one transaction, parking what can't apply yet ([D30](../../decisions.md#d30), [D59](../../decisions.md#d59)). A new deployable, `OpenPayments.Reconciler`, claims unresolved operations by lease, recovers them through each adapter's declared mode inside a per-account quota, and runs the timers, the parked-event worker and settlement ingestion ([D60](../../decisions.md#d60), [D62](../../decisions.md#d62), [D63](../../decisions.md#d63)). Every write still goes through `OpenPayments.Transitions`, and resolving an operation resolves its key in the same transaction ([D61](../../decisions.md#d61)). The staff console gains Needs attention review and two-person resolution.

**Tech Stack:** As Stage 1: .NET 10, ASP.NET Core, Npgsql, Dapper, PostgreSQL 18, PgBouncer, xUnit, FsCheck, Testcontainers, k6, Prometheus. Adds `Microsoft.Extensions.Hosting` worker services, `System.Threading.RateLimiting` token buckets, Playwright for the 3D Secure page, and the PSP's HTTP API called directly, with no PSP SDK unless Task 32's licence and .NET 10 check passes (S6).

**Spec:** [Implementation and benchmark design](../specs/2026-11-05-implementation-and-benchmark-design.md). Gate record: [Stage 2 design](../../gates/stage-2-design.md), which holds every decision this plan relies on. The system is defined in [architecture](../../architecture.md#psp-integration), [payment lifecycle](../../payment-lifecycle.md), [failure scenarios](../../failure-scenarios.md) and [verification](../../verification.md#build-phases). When this plan and a doc disagree, the doc wins: stop and escalate.

| Gate | Tasks | Done by | Exit record |
|---|---|---|---|
| Design | This plan | Opus | [`docs/gates/stage-2-design.md`](../../gates/stage-2-design.md), signed by the owner |
| Build | 19-37, console C3, C5, C12 | DeepSeek | `docs/gates/stage-2-progress.md`, with no open `docs/gates/stage-2-escalations.md` entry |
| Review | The complete change since the starting commit named in the gate record | Opus, in a fresh session | `docs/gates/stage-2-review.md`, with no open critical or high finding |

**Starts after:** the owner signs the Stage 2 design gate, naming the starting commit, with no `NEEDS-OWNER` item a task depends on. Stage 1's [review gate](../../gates/stage-1-review.md) closed on 2026-11-10. Milestones E-H use the mock PSP only, so they don't wait on the PSP choice if the owner signs with O1 still open for Milestone I; Milestone I does.

**Stage 2 also builds:** the Phase 2 exit record `docs/phase-gates/phase-2.md`, the frontend task [F2](2026-11-05-open-payments-implementation-and-benchmark.md#frontend-tasks-by-stage), the stage benchmark, and the fixes for Stage 1 findings [L7 and L8](../../gates/stage-1-review.md#verdict).

**Naming:** console plan tasks are written "console Task C3"; verification tests are written "test C3". They are different things that share a letter.

## Global Constraints

- Every [Stage 1 Global Constraint](2026-11-05-open-payments-implementation-and-benchmark.md#global-constraints) applies, and the [console plan's](2026-11-06-console.md#global-constraints) apply to console work. Progress and escalations go in the Stage 2 records.
- Commit and push directly to `main`, staging explicit paths. Author `EganNguyen <tuannh2y@gmail.com>`. Every message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`. One progress line per task in `docs/gates/stage-2-progress.md`: task, commit, test command, result.
- **Don't change without approval:** `docs/decisions.md`, the spec, `api/openapi.yaml` (except Task 34's freeze), any gate value, the timer values in the [gate record](../../gates/stage-2-design.md#reconciler-and-timers), and `db/migrations/` beyond `0004_stage2.sql` and `0005_settlement.sql`. Never edit `0001`-`0003`.
- **Sandbox only, no live cards.** Only PSP sandbox or test-mode credentials are used. Startup refuses a PSP key with a live prefix (Stripe `sk_live_`, `rk_live_`) or a `psp_accounts` row with `mode = 'live'`. No card number is ever sent, logged or stored: 3D Secure tests use the PSP's test payment-method tokens. U8's log scan runs on every Stage 2 acceptance run.
- **PSP secrets** live in `deploy/compose/secrets/` (git-ignored) or environment variables, referenced by `psp_accounts.credential_ref` and `callback_secret_ref`, never in the database, a log line, an exception message, a test fixture or a recorded HTTP fixture. Recorded fixtures are scrubbed of `Authorization` and signature headers. The owner creates sandbox accounts and enters keys; builders never create accounts or type secrets into a page.
- **Callback endpoint limits:** 64 KB body; signature checked on the raw body before any database write; 5-minute timestamp tolerance; its own concurrency limit (100 per instance), answering 503 with `Retry-After` over it; 2xx only after `COMMIT`; no merchant API key accepted on it.
- **No network inside a transaction,** including the reconciler: claim, commit, call the PSP, then apply ([D62](../../decisions.md#d62)). No row lock is held across a PSP call.
- **Recovery never starts an authorize,** never replays outside the replay window, and never treats "not found" as failed unless the adapter declares that lookup authoritative ([D27](../../decisions.md#d27), [D60](../../decisions.md#d60)).
- Every resolving path resolves its key through the store ([D61](../../decisions.md#d61)). No new caller writes `idempotency_keys.resolved_at` directly; cancel and create-without-method keep resolving theirs on the request path, as today ([D58](../../decisions.md#d58)).
- `OpenPayments.Reconciler` references `Domain`, `Transitions` and `Psp`, never `Api` or `Console`. `OpenPayments.Checks` still references neither `Domain` nor `Transitions`.
- A PSP's 429 is never retried under a new key, and a PSP call that may have been sent is never answered with 4xx or 503 ([M8](../../gates/stage-1-review.md#m8-a-psp-answer-the-adapter-cant-read-escapes-as-500), H3).
- Test names carry the verification ID, such as `C46_...` or `E9_...`.

## Review Focus

Likely to bite and not named by any single test. Each has a test in the task shown.

1. **A callback and transaction B race on one attempt** (Tasks 21, 25). Whichever loses must neither drop the PSP's answer (L7) nor post a second ledger entry, and the client must never get a refusal after the PSP acted.
2. **A key that never resolves, or resolves early** (Task 20). An operation finished by a callback, the reconciler or a person must resolve its key in the same commit; the authorize half of a two-call capture must not. Otherwise the expiry job either never frees keys, or frees one whose capture is still live.
3. **Replay after the window, or a not-found taken as proof** (Task 27). The reconciler must stop replaying at the window and escalate, under every recovery mode, with the mock's late landing turned on (C44, C45).
4. **Two reconcilers, or a lapsed lease** (Task 27). Only one apply wins; the PSP sees the same key; no double ledger entry (C26).
5. **Expiry or escalation racing a capture, void or late void** (Tasks 28, 29). Expiry waits for a live capture, a late void never blocks a capture, and escalation never moves a payment whose attempt just resolved.
6. **The key-expiry job deleting an unresolved key** (Task 30), including one resolved in the same second as the scan, under concurrent load.
7. **Recovery starving live traffic during a PSP slowdown** (Tasks 23, 27). The per-account quota holds while thousands of attempts are `UNKNOWN` (C47), and one revoked credential opens only its account's breaker (C51).
8. **A settlement rerun or a killed ingest posting twice** (Task 31).

---

## Milestone E: Foundations (before callbacks and the reconciler)

L7 must be settled before callbacks and the reconciler exist, so this milestone comes first.

### Task 19: Stage 2 schema

**Files:**
- Create: `db/migrations/0004_stage2.sql`
- Test: `tests/OpenPayments.Integration.Tests/Stage2SchemaTests.cs`

**Interfaces:**
- Produces, in `0004_stage2.sql`:
  - `psp_events` recreated, hash-partitioned 16 ways by `psp_event_id` (it has never held rows): `psp_account_id uuid not null`, `psp_event_id text not null`, `source text not null check (source in ('callback','api_answer'))`, `payload jsonb not null`, `payment_id uuid`, `attempt_id uuid`, `refund_id uuid`, `received_at timestamptz not null default now()`, `processed_at timestamptz`, `outcome text check (outcome in ('applied','ignored','contradiction','parked','expired'))`, `attempts int not null default 0`, `next_attempt_at timestamptz`, `last_error text`, primary key `(psp_account_id, psp_event_id)`. Indexes `psp_events_parked on (next_attempt_at) where processed_at is null`, `psp_events_payment on (payment_id) where payment_id is not null`, `psp_events_expirable on (processed_at) where processed_at is not null`.
  - `payment_attempts` and `refunds` gain `idempotency_key text`, `resolves_key boolean not null default true`, `next_recovery_at timestamptz`, `recovery_attempts int not null default 0`, `last_inquiry_at timestamptz`, `last_inquiry_result text`.
  - Each `idempotency_keys` partition gets `autovacuum_vacuum_scale_factor = 0.01` and `autovacuum_vacuum_insert_scale_factor = 0.01`, and so does each `psp_events` partition ([key expiry](../../gates/stage-2-design.md#key-expiry-decided-by-analysis)).
  - `psp_accounts` gains `callback_secret_ref text`, `recovery_share numeric(3,2) not null default 0.10 check (recovery_share > 0 and recovery_share < 1)`.
  - Grants: `op_app` gets `select, insert, update, delete` on `psp_events`; `op_console_read` gets `select` on `psp_events`.
- All columns added nullable or with constants, so no table rewrite on `payments` or `payment_attempts`. Run `scripts/check-migration.sh`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Psp_events_primary_key_dedupes_per_account();
[Fact] public Task Psp_events_is_partitioned_and_keeps_partition_key_in_primary_key();
[Fact] public Task Psp_events_parked_and_payment_indexes_exist();             // EXPLAIN on a seeded table
[Fact] public Task Attempts_and_refunds_carry_key_and_recovery_columns();
[Fact] public Task Recovery_share_out_of_range_is_rejected();
[Fact] public Task Key_and_event_partitions_have_expiry_autovacuum_settings();
[Fact] public Task Console_read_role_can_read_psp_events_but_not_write();
[Fact] public Task Op_app_money_grants_outside_psp_events_are_unchanged();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test tests/OpenPayments.Integration.Tests --filter Stage2SchemaTests`. Expected: FAIL, the columns don't exist.
- [ ] **Step 3: Write the migration.** Lock timeouts from S4 at the top; no `create index concurrently` inside a transaction block.
- [ ] **Step 4: Verify:** the tests pass, `scripts/check-migration.sh db/migrations/0004_stage2.sql` exits 0, and the whole suite passes.
- [ ] **Step 5: Commit** `db: add Stage 2 schema for events, recovery and key links`.

### Task 20: One key-resolution path (D61)

**Files:**
- Create: `src/OpenPayments.Transitions/AnswerRenderer.cs`
- Modify: `src/OpenPayments.Transitions/PaymentStore.cs`, `src/OpenPayments.Api/PaymentFlow.cs`, `src/OpenPayments.Api/ApiJson.cs`
- Test: `tests/OpenPayments.Integration.Tests/KeyResolutionTests.cs`, `tests/OpenPayments.Acceptance.Tests/KeyResolutionAcceptanceTests.cs`

**Interfaces:**
- Produces:
  - `public static StoredResponse AnswerRenderer.Render(PaymentView payment, OperationRef operation)`, where `OperationRef` is `Attempt(AttemptId, AttemptOperation)` or `Refund(RefundId)`. The API's existing body shapes move here unchanged, byte for byte.
  - `BeginAttempt` and the refund and operation begin commands take `string IdempotencyKey` and `bool ResolvesKey`, written to the new columns in transaction A.
  - Inside `ApplyInTransactionAsync`: when the result moves an attempt or refund to `SUCCEEDED` or `FAILED` and that row has `resolves_key`, update `idempotency_keys` by `(merchant_id, key)` where `resolved_at is null`, with `AnswerRenderer.Render`. `KeyAnswer` stays only to let the request path override the rendered body where it differs today; it may not skip resolution.
  - `StoreResponseAsync`'s update by `attempt_id` or `refund_id` is removed.

- [ ] **Step 1: Write failing tests**

```csharp
[Theory, MemberData(nameof(EveryResolvingCommand))]   // PSP answer, recovery answer, void answer, refund confirm,
public Task Resolving_command_sets_resolved_at_in_same_commit(string command); // refund release, manual resolution, late reversal
[Fact] public Task Timeout_and_escalation_leave_key_unresolved();
[Fact] public Task Two_call_authorize_success_leaves_key_unresolved_until_capture();
[Fact] public Task Key_resolved_by_reconciler_path_replays_same_body_as_api_path();
[Fact] public Task Already_resolved_key_is_not_overwritten();
[Fact] public Task Key_update_uses_primary_key_not_attempt_scan();               // EXPLAIN: idempotency_keys_pkey
[Fact] public Task D58_answers_are_unchanged();                                   // existing D58KeyAnswerTests still pass
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "KeyResolution"`. Expected: FAIL, the resolution happens only on the request path.
- [ ] **Step 3: Implement.** Move the renderer, write the key in transaction A, resolve centrally.
- [ ] **Step 4: Verify:** the filter passes, then `dotnet test OpenPayments.slnx` and S3's query-count ceilings. A changed ceiling is an escalation, not an edit ([D48](../../decisions.md#d48)).
- [ ] **Step 5: Commit** `feat: resolve keys wherever an operation finishes (D61)`.

**Closes:** E3's `resolved_at` check, with Task 30.

### Task 21: A PSP answer is never dropped (L7, D59)

**Files:**
- Create: `src/OpenPayments.Transitions/PspEventStore.cs`
- Modify: `src/OpenPayments.Transitions/PaymentStore.cs`, `src/OpenPayments.Api/PaymentFlow.cs`
- Test: `tests/OpenPayments.Acceptance.Tests/L7AnswerKeptTests.cs`

**Interfaces:**
- Produces:
  - `Task<ApplyResult> PaymentStore.ApplyPspAnswerAsync(PaymentId p, PaymentCommand answer, KeyAnswer? key, CancellationToken ct)`: apply; on `AlreadyMoved` or `Rejected`, re-read and apply once more; if still refused, call `PspEventStore.ParkApiAnswerAsync`.
  - `Task PspEventStore.ParkApiAnswerAsync(PspAccountId account, OperationRef op, PaymentCommand answer, CancellationToken ct)`: inserts `psp_events` with `source = 'api_answer'`, `psp_event_id = "api:{op}:{id}"`, `outcome = 'parked'`, `next_attempt_at = now()`; `ON CONFLICT DO NOTHING`.
  - `FromAppliedAsync` and `FromRefundAppliedAsync` answer 202 with the current state when the answer was parked, never 409 or 422 after a PSP call.

- [ ] **Step 1: Write failing tests,** using the store's existing failpoints to change the payment between the PSP call and transaction B

```csharp
[Fact] public Task L7_lost_conditional_update_is_reapplied_once_from_fresh_snapshot();
[Fact] public Task L7_answer_refused_twice_is_parked_and_client_gets_202();
[Fact] public Task L7_already_applied_by_another_writer_answers_current_state_with_no_second_ledger();
[Fact] public Task L7_park_failure_still_answers_202_and_leaves_attempt_unresolved();
[Theory, InlineData("capture"), InlineData("void"), InlineData("refund"), InlineData("authorize"), InlineData("sale")]
public Task L7_no_route_answers_4xx_after_the_psp_answered(string route);
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter L7`. Expected: FAIL with 409.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify:** the filter and the whole suite pass, including the review tests in `tests/OpenPayments.Acceptance.Tests/Review/`.
- [ ] **Step 5: Commit** `fix: keep the PSP's answer when transaction B loses (L7, D59)`.

### Task 22: Two-call capture's first read (L8)

**Files:**
- Modify: `src/OpenPayments.Api/PaymentFlow.cs` (`CaptureCoreAsync`, `CaptureForCreateAsync`), `src/OpenPayments.Transitions/PaymentStore.cs` (a test-only read failpoint `BeforeRead`)
- Test: `tests/OpenPayments.Acceptance.Tests/L8TwoCallCaptureTests.cs`

- [ ] **Step 1: Write failing tests,** with the mock configured `supports_sale = false`

```csharp
[Fact] public Task L8_database_failure_on_capture_first_read_after_authorization_answers_202();
[Fact] public Task L8_same_key_retry_after_window_captures_once();
[Fact] public Task L8_answer_carries_payment_id_so_the_client_can_poll_instead_of_retrying();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter L8`. Expected: the first fails with 503.
- [ ] **Step 3: Implement:** every exception after the authorization was sent takes the 202 path in `AfterPspAsync`, as M8 did for the PSP call itself.
- [ ] **Step 4: Verify** the filter and whole suite pass.
- [ ] **Step 5: Commit** `fix: answer 202 when a two-call capture's read fails (L8)`.

### Task 23: Adapter contract, resilience per account

**Files:**
- Modify: `src/OpenPayments.Psp/Contracts.cs`, `src/OpenPayments.Psp/MockPspAdapter.cs`, `src/OpenPayments.Api/Program.cs`, `src/OpenPayments.Api/Metrics.cs`, `deploy/compose/prometheus/alerts.yml`, `docs/metrics.md`
- Create: `src/OpenPayments.Psp/Resilience/AccountGate.cs`, `src/OpenPayments.Psp/Resilience/Breaker.cs`, `src/OpenPayments.Psp/ResilientPspAdapter.cs`
- Test: `tests/OpenPayments.MockPsp.Tests/ResilienceTests.cs`, `tests/OpenPayments.Acceptance.Tests/PspResilienceTests.cs`

**Interfaces:**
- `PspCapabilities` becomes `(bool SupportsSale, RecoveryMode Unanswered, bool ConsistentLookupByReference, ActionProof ActionProof, TimeSpan KeyRetention, TimeSpan ReplayWindow, TimeSpan AuthorizationWindow, TimeSpan SettlementLag, TimeSpan LookupQuietWindow)` with `enum RecoveryMode { Replay, AuthoritativeLookup, TechnicalCancel }` and `enum ActionProof { None, ConfirmedCancel }` ([D60](../../decisions.md#d60)). Construction throws if `ReplayWindow > KeyRetention / 4` or `ReplayWindow <= attempt timeout`.
- `IPspAdapter` gains `Task<PspInquiry> LookupByReferenceAsync(string pspReference, CancellationToken ct)` and `Task<PspInquiry> CancelForProofAsync(AttemptId attempt, string? pspReference, CancellationToken ct)`.
- `AccountGate`: per `psp_accounts.id`, a token bucket split into `Live` (`1 - recovery_share`) and `Recovery` (`recovery_share`) of `rate_limit_per_s ÷ instance count`; a breaker per PSP and one per account. `Task<Lease> AcquireAsync(PspAccountId a, Lane lane, CancellationToken ct)` fails fast with `PspRejectedException(reason)` when the bucket is empty or a breaker is open.
- Credential rejection from the PSP (`401`/`403`) opens only the account's breaker and maps to the stable code `psp_credential_rejected`, with the attempt `FAILED`, no `UNKNOWN` (C51). It is known before the PSP acted, so it is the one post-call failure that may answer 402.
- A PSP 429 honours `Retry-After`, opens nothing, and never retries under a new key (C12).
- `SocketsHttpHandler.PooledConnectionLifetime = 1 min`; connect failures retry, since nothing was sent (C16). Metrics `op_psp_breaker_state{psp,account}`, `op_psp_rejected_total{reason}`, `op_psp_tls_cert_expiry_seconds{psp}`; alerts `PspBreakerOpen`, `PspAccountCredentialRejected`, `PspTlsCertExpiring`, `PspDnsFailures`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void U5_capabilities_reject_replay_window_beyond_quarter_retention();
[Fact] public Task C12_psp_429_backs_off_by_retry_after_with_same_key_and_no_duplicate_effect();
[Fact] public Task C13_psp_outage_opens_psp_breaker_and_fails_fast_with_retry_after();
[Fact] public Task C13_failover_routes_new_payment_to_secondary_mock_only_where_merchant_has_account();
[Fact] public Task C51_revoked_credential_fails_that_merchant_fast_with_stable_code_and_no_unknown();
[Fact] public Task C51_revoked_credential_opens_account_breaker_only_and_others_keep_succeeding();
[Fact] public Task C16_dns_change_is_picked_up_within_connection_lifetime();
[Fact] public Task C16_tls_cert_expiry_metric_and_alert_rule_present();          // promtool test rules
[Fact] public Task Recovery_lane_cannot_take_live_lane_tokens();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "U5_|C12_|C13_|C16_|C51_|Recovery_lane"`.
- [ ] **Step 3: Implement.** The factory wraps every adapter in `ResilientPspAdapter`.
- [ ] **Step 4: Verify:** filter, whole suite, `promtool check rules deploy/compose/prometheus/alerts.yml`.
- [ ] **Step 5: Commit** `feat: per-account PSP limits, breakers and recovery capabilities`.

### Task 24: Mock PSP for Stage 2

**Files:**
- Modify: `tools/OpenPayments.MockPsp/Program.cs`, `Outcomes.cs`, `AttemptStore.cs`, `src/OpenPayments.Psp/MockPspAdapter.cs`
- Create: `tools/OpenPayments.MockPsp/Signing.cs`, `tools/OpenPayments.MockPsp/Settlement.cs`
- Test: `tests/OpenPayments.MockPsp.Tests/Stage2MockTests.cs`

**Interfaces:**
- Callbacks per account: `PUT /admin/accounts/{account}` gains `callback_url` and `signing_secret`. Each callback carries `event_id` (unique per delivery content, stable across redelivery), `type`, `reference` (our attempt or refund ID), `psp_reference`, `amount_minor`, `currency`, and header `Mock-Signature: t=<unix>,v1=<hex HMAC-SHA256 of "t.body">`.
- Config gains `callback_duplicate_rate`, `callback_reorder_window_ms`, `callback_redeliver_until_s` (default 3 days, scaled by `time_scale`), `supports_sale`, `recovery_mode`, `consistent_lookup`, `action_proof`, `authorization_window_s`.
- `GET /lookup/{psp_reference}`, `POST /cancel-for-proof/{key}`.
- `GET /admin/settlement/{account}?date=YYYY-MM-DD&defects=dup,unmatched,amount,missing` returns a CSV of that day's captures, sales and refunds, with the requested defects.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Callback_is_signed_with_account_secret_and_verifies();
[Fact] public Task Redelivered_callback_keeps_its_event_id();
[Fact] public Task Duplicate_and_reorder_rates_produce_duplicates_and_out_of_order_delivery();
[Fact] public Task Callbacks_go_to_each_accounts_own_url();
[Fact] public Task Supports_sale_false_refuses_sale();
[Fact] public Task Lookup_by_psp_reference_is_consistent_when_configured();
[Fact] public Task Cancel_for_proof_blocks_late_landing_and_confirms();
[Fact] public Task Settlement_csv_contains_requested_defects_and_is_stable_for_same_date();
[Fact] public Task Authorization_lapses_after_window_and_capture_is_declined();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test tests/OpenPayments.MockPsp.Tests --filter Stage2MockTests`.
- [ ] **Step 3: Implement.** Keep `evicted` distinct from `unknown_attempt`.
- [ ] **Step 4: Verify** the mock suite passes, and repeat the mock capacity preview at 10,000 req/s with callbacks on; record in `benchmarks/mock-psp-capacity.md`.
- [ ] **Step 5: Commit** `feat: signed per-account callbacks, recovery modes and settlement files in the mock`.

---

## Milestone F: Callbacks

### Task 25: Callback receiver

**Files:**
- Create: `src/OpenPayments.Api/Endpoints/PspEvents.cs`, `src/OpenPayments.Psp/IPspEventParser.cs`, `src/OpenPayments.Psp/MockPspEventParser.cs`
- Modify: `src/OpenPayments.Transitions/PspEventStore.cs`, `src/OpenPayments.Api/Program.cs`, `src/OpenPayments.Api/Metrics.cs`, `deploy/compose/prometheus/alerts.yml`, `docs/metrics.md`, `docs/architecture.md` (route name only, in PSP integration)
- Test: `tests/OpenPayments.Acceptance.Tests/CallbackTests.cs`, `tests/OpenPayments.Domain.Tests/CallbackPermutationTests.cs`

**Interfaces:**
- Route `POST /psp/v1/accounts/{psp_account_id:guid}/events`.
- `IPspEventParser.Parse(ReadOnlySpan<byte> rawBody, IReadOnlyDictionary<string,string> headers, string signingSecret, DateTimeOffset now) -> Result<PspEvent>`; `PspEvent(string EventId, PspEventKind Kind, OperationRef Reference, string? PspReference, Money? Amount, string? FailureCode, string? ActionType)`; `PspEventKind { Authorized, Captured, SaleCaptured, Declined, ActionRequired, Voided, VoidFailed, Refunded, RefundFailed, Canceled }`.
- `Task<EventOutcome> PspEventStore.ReceiveAsync(PspAccountId account, PspEvent e, string payload, CancellationToken ct)`: one `TxRunner` transaction: insert (`ON CONFLICT DO NOTHING`; a duplicate returns `Duplicate`), map to a command, `Decide`, conditional update, ledger, outbox, key (D61), set `processed_at` and `outcome`, or leave it parked with `next_attempt_at`.
- Mapping: a stale or same-state event is `ignored`; one contradicting a final state is `contradiction` with the FS-05 alert; one the operation can't take yet, or a `55P03`, is `parked`. Amount or currency mismatch goes to `REVIEW` (C32). An approval on a `FAILED` attempt takes the late-approval path (C38, E19).
- Metrics `op_psp_events_total{source,outcome}`, `op_psp_events_parked`, `op_psp_events_parked_oldest_age_seconds`; alerts `PspEventContradiction`, `PspEventsParkedTooLong`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Bad_signature_is_401_and_writes_nothing();
[Fact] public Task Stale_timestamp_is_401();
[Fact] public Task Unknown_or_disabled_account_is_404_and_writes_nothing();
[Fact] public Task Body_over_64kb_is_413();
[Fact] public Task Over_callback_concurrency_limit_is_503_with_retry_after();
[Fact] public Task Event_applies_in_same_transaction_as_insert();                       // failpoint between: nothing committed
[Fact] public Task Duplicate_event_is_200_and_changes_nothing();
[Fact] public Task C9_duplicate_delayed_and_reordered_callbacks_reach_one_state_and_one_ledger_entry();
[Fact] public Task C46_kill_between_receipt_and_commit_then_redelivery_applies_once();     // CrashHarness
[Fact] public Task C46_callback_before_transaction_b_applies_or_parks_and_b_answers_current_state();
[Fact] public Task E9_late_declined_callback_on_captured_payment_is_stored_logged_counted_and_alerts(); // per E2's answer
[Fact] public Task E10_callback_the_operation_cannot_take_yet_is_parked();
[Fact] public Task C32_amount_or_currency_mismatch_moves_to_review_with_no_ledger_and_alerts();
[Fact] public Task C38_late_approval_on_failed_attempt_goes_to_review_with_no_ledger();
[Fact] public Task E19_payment_awaiting_method_moves_to_review_on_late_approval();
[Fact] public Task Callback_resolves_the_operations_key();
// Domain: every permutation of {duplicate, delayed, reordered} events per operation kind reaches one final state
[Property] public bool C9_all_permutations_converge(PspEventKind[] events);
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "CallbackTests|CallbackPermutationTests"`. Expected: 404, no route.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** the filter and the whole suite, and `promtool check rules`.
- [ ] **Step 5: Commit** `feat: callback receiver with atomic apply and parking (D30, D59)`.

### Task 26: Reconciler host and the parked-event worker

**Files:**
- Create: `src/OpenPayments.Reconciler/OpenPayments.Reconciler.csproj`, `Program.cs`, `ParkedEventWorker.cs`, `ReconcilerOptions.cs`, `deploy/compose/reconciler.yaml`, `tests/OpenPayments.Reconciler.Tests/OpenPayments.Reconciler.Tests.csproj`, `tests/OpenPayments.Reconciler.Tests/ParkedEventWorkerTests.cs`
- Modify: `OpenPayments.slnx`, `tests/OpenPayments.Analyzers.Tests/BannedApiTests.cs` (request-path rules apply to the reconciler), `docs/architecture/codebase-structure.md`

**Interfaces:**
- `ReconcilerOptions`: `Tick` 2 s, `Batch` 100, `Lease` 60 s, `AttemptTimeout` 30 s, `ParkedLimit` 15 min, `EscalateAfter` 2 h, `KeyRetention` 24 h (startup refuses less), `EventRetention` 7 days. Values from the [gate record](../../gates/stage-2-design.md#reconciler-and-timers).
- `ParkedEventWorker.RunOnceAsync(DateTimeOffset now, CancellationToken ct) -> Task<int>`: claims `psp_events where processed_at is null and next_attempt_at <= now` with `for update skip locked limit Batch`, re-applies each through `PspEventStore`, backs off 1, 2, 4 ... 60 s, and past `ParkedLimit` sets `outcome = 'expired'` and counts it.
- The project references `Domain`, `Transitions`, `Psp` only; an architecture test enforces it.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public void Reconciler_references_no_api_or_console();
[Fact] public Task Parked_event_applies_once_its_operation_can_take_it();
[Fact] public Task Parked_event_backs_off_between_tries();
[Fact] public Task Parked_event_past_limit_expires_alerts_and_leaves_operation_unresolved();
[Fact] public Task Api_answer_parked_by_L7_is_applied_by_the_worker();
[Fact] public Task Two_workers_never_apply_one_event_twice();
[Fact] public Task Startup_refuses_key_retention_below_24h();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test tests/OpenPayments.Reconciler.Tests`. Expected: FAIL to compile.
- [ ] **Step 3: Implement,** with `deploy/compose/reconciler.yaml` starting it beside the API.
- [ ] **Step 4: Verify** the whole suite and `dotnet build -warnaserror`.
- [ ] **Step 5: Commit** `feat: add the reconciler host and parked-event worker`.

---

## Milestone G: Reconciler and timers

### Task 27: Recovery by lease, mode and quota

**Files:**
- Create: `src/OpenPayments.Reconciler/RecoveryWorker.cs`, `src/OpenPayments.Transitions/RecoveryStore.cs`
- Test: `tests/OpenPayments.Reconciler.Tests/RecoveryTests.cs`, `tests/OpenPayments.Integration.Tests/RecoveryCrashTests.cs`

**Interfaces:**
- `RecoveryStore.ClaimAsync(DateTimeOffset now, int batch, TimeSpan lease, CancellationToken ct) -> Task<IReadOnlyList<RecoveryItem>>`: one short transaction over `payment_attempts_unresolved` and `refunds_unresolved`, `created_at < now - AttemptTimeout` and `coalesce(next_recovery_at, '-infinity') <= now`, `for update skip locked`, sets `next_recovery_at = now + lease` and `recovery_attempts + 1`, commits.
- `RecoveryItem(OperationRef Op, PaymentId Payment, PspAccountId Account, AttemptState State, string? PspReference, DateTimeOffset CreatedAt, Money Amount, ...)`.
- `RecoveryWorker.RunOnceAsync(DateTimeOffset now, CancellationToken ct) -> Task<RecoverySummary>`: per item, first applies `Timeout` if still `PENDING`; then chooses by [D60](../../decisions.md#d60): with a PSP reference and `ConsistentLookupByReference`, `LookupByReferenceAsync`; else by `Unanswered`: `Replay` only while `now < CreatedAt + ReplayWindow`, `AuthoritativeLookup` with not-found as proof only after `LookupQuietWindow`, `TechnicalCancel` with proof on confirmed cancel. Spends the account's `Recovery` lane; an empty bucket leaves the item for the next lease. Applies through `PaymentStore.ApplyPspAnswerAsync` (so L7 holds) and writes `last_inquiry_at`, `last_inquiry_result`.
- Late voids ([D37](../../decisions.md#d37)): an authorize attempt in `REVIEW` from a late approval, where the adapter allows void, gets a `Void` attempt with `target_attempt_id`, under its own key. A late sale is left for a person.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task C3_approve_then_timeout_on_authorize_capture_and_refund_resolves_each_with_one_effect();
[Fact] public Task C4_kill_api_after_psp_approved_reconciler_recovers_one_charge();          // CrashHarness
[Fact] public Task C15_failure_in_transaction_b_reconciler_completes_with_no_partial_ledger();
[Theory, InlineData("Replay"), InlineData("AuthoritativeLookup"), InlineData("TechnicalCancel")]
public Task C44_not_found_then_late_landing_never_fails_attempt_without_proof(string mode);
[Theory, InlineData("Replay"), InlineData("AuthoritativeLookup"), InlineData("TechnicalCancel")]
public Task C45_past_replay_window_no_replay_is_sent_and_attempt_stays_unknown(string mode);
[Fact] public Task C26_backlog_drains_at_rate_cap_and_two_reconcilers_act_once_per_item();
[Fact] public Task C26_lapsed_lease_repeats_call_under_same_key_with_one_effect_and_one_ledger();
[Fact] public Task C47_thousands_unknown_recovery_stays_inside_quota_and_live_keeps_its_share(); // CK2, CK5
[Fact] public Task C34_capture_refund_and_reconciler_race_with_forced_lock_collision();        // 40P01 counted, ≤2 retries, 55P03 → 503
[Fact] public Task C36_two_captures_different_keys_one_effect_with_reconciler_running();
[Fact] public Task C37_decline_then_two_new_attempts_one_effect_with_reconciler_running();
[Fact] public Task C38_late_approval_is_voided_automatically_and_void_does_not_block_capture();
[Fact] public Task C38_late_approved_sale_stays_in_review_for_a_person();
[Fact] public Task Last_inquiry_is_recorded_on_the_row();
[Fact] public Task Reconciler_resolves_the_operations_key();
[Fact] public Task Reconciler_never_starts_an_authorize();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "RecoveryTests|RecoveryCrashTests"`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** the filter, the whole suite, and `op-checks --stage phase2` after C47's run with CK2 and CK5 passing.
- [ ] **Step 5: Commit** `feat: recover unresolved operations by lease, mode and quota (D60, D62)`.

### Task 28: Timers: action window, payment window, authorization expiry

**Files:**
- Create: `src/OpenPayments.Reconciler/TimerWorker.cs`
- Modify: `src/OpenPayments.Transitions/PaymentStore.cs` (write `authorized_until` in transaction B), `src/OpenPayments.Domain/Decide.cs` only if a listed transition is missing, with the lifecycle row cited
- Test: `tests/OpenPayments.Reconciler.Tests/TimerTests.cs`, `tests/OpenPayments.Acceptance.Tests/AuthorizedUntilTests.cs`

**Interfaces:**
- `TimerWorker.RunOnceAsync(DateTimeOffset now, CancellationToken ct)`: action window through `payment_attempts_action_window` (`ACTION_REQUIRED → UNKNOWN`); payment window through `payments_awaiting` (`AWAITING_METHOD → CANCELED`); authorization window through `payments_authorized` (`AUTHORIZED → EXPIRED`). Each batch uses `for update skip locked` and the same conditional update as any writer.
- `authorized_until = authorized_at + adapter.Capabilities.AuthorizationWindow`, written whenever a payment becomes `AUTHORIZED`, by any path.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Authorized_until_is_set_by_api_callback_and_reconciler_paths();
[Fact] public Task Action_window_end_moves_to_unknown_never_failed();
[Fact] public Task Payment_window_end_cancels_awaiting_method();
[Fact] public Task C50_authorization_window_passes_during_live_capture_expiry_waits();
[Fact] public Task C50_capture_and_void_at_once_one_psp_effect_loser_gets_status_or_409();
[Fact] public Task C50_void_timeout_stays_unresolved_with_no_state_change();
[Fact] public Task C50_capture_after_voided_or_expired_is_rejected();
[Fact] public Task C50_auth_hold_clears_in_every_final_state();                       // CK7
[Fact] public Task Timer_and_manual_capture_cannot_both_win();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "TimerTests|AuthorizedUntilTests"`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** the whole suite.
- [ ] **Step 5: Commit** `feat: action, payment and authorization timers`.

### Console Task C3: Escalation and manual resolution in `Decide`

Build [console Task C3](2026-11-06-console.md#task-c3-escalation-and-manual-resolution-in-decide) as written, now. **It must land before Task 29**, whose escalation job issues its commands. Its tests are `ManualResolutionTests`.

### Task 29: Escalation past the age limit (D45)

**Files:**
- Create: `src/OpenPayments.Reconciler/EscalationWorker.cs`
- Test: `tests/OpenPayments.Reconciler.Tests/EscalationTests.cs`

**Interfaces:**
- `EscalationWorker.RunOnceAsync(DateTimeOffset now, CancellationToken ct)`: for authorize and sale attempts `UNKNOWN` past `EscalateAfter`, issues `Escalate`; for capture attempts, `Escalate` on an `AUTHORIZED` payment; for refunds, `RefundEscalate`. Void attempts are never escalated. Metric `op_escalations_total{kind}` and alert `UnresolvedEscalated`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Unknown_sale_past_age_limit_moves_to_review_with_no_ledger();
[Fact] public Task Unknown_capture_past_age_limit_moves_authorized_payment_to_review();
[Fact] public Task Unknown_refund_past_age_limit_moves_refund_only();
[Fact] public Task Stuck_void_is_never_escalated();
[Fact] public Task Attempt_resolved_just_before_escalation_is_not_moved();          // Review Focus 5
[Fact] public Task Escalation_leaves_key_unresolved();
[Fact] public Task Escalated_case_appears_in_needs_review_queue();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter EscalationTests`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** the whole suite.
- [ ] **Step 5: Commit** `feat: escalate unresolved operations past the age limit (D45)`.

### Task 30: Key and event expiry

The method is [decided by analysis](../../gates/stage-2-design.md#key-expiry-decided-by-analysis) in the gate record ([D64](../../decisions.md#d64)). Build it as written; don't benchmark it in this task.

**Files:**
- Create: `src/OpenPayments.Reconciler/ExpiryWorker.cs`
- Test: `tests/OpenPayments.Reconciler.Tests/ExpiryTests.cs`, `tests/OpenPayments.Acceptance.Tests/C31bKeyExpiryTests.cs`

**Interfaces:**
- `ExpiryWorker.RunOnceAsync(DateTimeOffset now, CancellationToken ct) -> Task<ExpirySummary>`: loops over the 16 `idempotency_keys` partitions and deletes batches of `Expiry:Batch` (500) keys with `resolved_at < now - KeyRetention`, oldest first, `for update skip locked`, each batch its own transaction, paced to `Expiry:MaxDeletesPerSecond`. A row with `resolved_at is null` can't match the statement, so no prior read decides it. Processed `psp_events` older than `EventRetention` go the same way; a parked one never does. Metrics `op_keys_expired_total`, `op_keys_expirable_backlog`, `op_expiry_batch_seconds`; alert `KeyExpiryFallingBehind`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task E4_resolved_key_past_retention_is_removed_and_reuse_creates_new_operation();
[Fact] public Task E4_resolved_key_just_inside_retention_replays_with_no_psp_call();
[Fact] public Task E4_unresolved_key_is_kept_whatever_its_age();
[Fact] public Task C41_unknown_payment_past_24h_key_kept_retry_returns_state_no_second_charge();
[Fact] public Task E24_key_older_than_24h_with_unknown_payment_is_kept();
[Fact] public Task Key_resolved_during_the_batch_is_not_deleted_early();             // concurrent resolve, Review Focus 6
[Fact] public Task Parked_events_are_never_deleted();
[Fact] public Task Delete_runs_per_partition_oldest_first_using_partition_index();      // EXPLAIN per partition
[Fact] public Task Expiry_respects_max_deletes_per_second();
[Fact] public Task Backlog_metric_and_falling_behind_alert_fire_when_expiry_is_stopped();  // promtool test rules
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "ExpiryTests|C31bKeyExpiryTests"`.
- [ ] **Step 3: Implement** the method the gate record names.
- [ ] **Step 4: Verify** the whole suite.
- [ ] **Step 5: Commit** `feat: expire resolved keys and processed events in batches (D19)`.

---

## Milestone H: Settlement

### Task 31: Settlement ingestion per account (D63)

**Files:**
- Create: `db/migrations/0005_settlement.sql`, `src/OpenPayments.Reconciler/SettlementWorker.cs`, `src/OpenPayments.Transitions/SettlementStore.cs`, `src/OpenPayments.Psp/ISettlementSource.cs`, `src/OpenPayments.Psp/MockSettlementSource.cs`, `tools/OpenPayments.Checks/sql/ck8.sql` (replacing its not-applicable stub)
- Modify: `src/OpenPayments.Domain/Commands.cs`, `Decide.cs`, `Postings.cs` (a `SettlementMatched` command that never changes payment state)
- Test: `tests/OpenPayments.Reconciler.Tests/SettlementTests.cs`, `tests/OpenPayments.Domain.Tests/SettlementPostingTests.cs`, `tests/OpenPayments.Checks.Tests/CorruptionTests.cs` (CK8 cases)

**Interfaces:**
- `0005_settlement.sql`: `settlement_files(psp_account_id uuid, file_id text, business_date date, state text check in ('receiving','matching','done'), lines_total int, received_at, completed_at, primary key (psp_account_id, file_id))` and `settlement_missing(psp_account_id, attempt_id uuid null, refund_id uuid null, payment_id uuid not null, expected_by timestamptz, found_at timestamptz null, primary key (...))`.
- `ISettlementSource.ListFilesAsync(PspAccount a, DateOnly from, CancellationToken ct)` and `ReadAsync(PspAccount a, string fileId, CancellationToken ct) -> IAsyncEnumerable<SettlementLine>`.
- `SettlementStore.IngestLineAsync`, `MatchPendingAsync(file)`, `FindMissingAsync(now)`. A matched line posts with ledger ID `UUIDv5(psp_account_id, file_id, line_no)`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task C40_duplicate_unmatched_wrong_amount_and_missing_lines_each_end_in_defined_state();
[Fact] public Task C40_ingesting_same_file_twice_posts_nothing_new();                 // E23
[Fact] public Task C40_killed_ingest_resumes_and_posts_each_line_once();
[Fact] public Task C40_discrepancy_pages();                                         // alert rule fires in promtool test
[Fact] public Task E22_unmatched_or_mismatched_line_posts_nothing();
[Fact] public Task Settlement_never_changes_payment_state();
[Fact] public Task Fee_lines_are_stored_and_post_nothing();
[Fact] public Task CK8_detects_a_late_file_and_fails_on_zero_rows();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test --filter "Settlement|CK8"`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** the whole suite and `scripts/check-migration.sh db/migrations/0005_settlement.sql`.
- [ ] **Step 5: Commit** `feat: ingest settlement files per account (D18, D63)`.

---

## Milestone I: The real PSP

**Needs:** O1, O2, O3 and O5 answered in the gate record. The tasks name Stripe, the recommendation; if the owner picks another PSP, stop and escalate for this milestone only.

### Task 32: Stripe sandbox adapter

**Files:**
- Create: `src/OpenPayments.Psp/Stripe/StripeAdapter.cs`, `StripeEventParser.cs`, `StripeSettlementSource.cs`, `StripeCapabilities.cs`, `tests/OpenPayments.Psp.Tests/OpenPayments.Psp.Tests.csproj`, `tests/OpenPayments.Psp.Tests/StripeContractTests.cs`, `tests/OpenPayments.Psp.Tests/fixtures/stripe/*.json`, `tests/OpenPayments.Psp.Tests/StripeSandboxTests.cs`
- Modify: `OpenPayments.slnx`, `src/OpenPayments.Api/Program.cs` (factory by `psp_accounts.psp`)

**Interfaces:**
- PaymentIntents: sale is automatic capture with confirm; authorize is manual capture; capture, cancel (void) and refund by PSP ID. `attempt_id` is the `Idempotency-Key` and goes into metadata; `refund_id` likewise. Capabilities: `Replay` with no reference, consistent lookup by PaymentIntent ID, `ConfirmedCancel` for abandoned actions, retention 24 h, replay window 1 h, authorization window 6 days. Recheck each [Stripe fact](../../architecture.md#psp-integration) and record the date in `StripeCapabilities.cs`.
- Startup refuses a live key prefix. The adapter maps every unreadable answer to `PspUnavailableException` (M8).

- [ ] **Step 1: Write failing contract tests (U5)** against recorded, scrubbed fixtures

```csharp
[Fact] public void U5_capabilities_match_checked_facts();
[Theory, MemberData(nameof(Fixtures))] public void U5_each_recorded_answer_maps_to_expected_result(string fixture);
[Fact] public void U5_lower_case_and_unknown_currency_is_inconclusive_never_500();      // R10's case
[Fact] public void U5_signed_event_verifies_and_tampered_body_fails();
[Fact] public void U5_amount_conversion_per_currency_jpy_usd_kwd();
[Fact] public void Live_key_prefix_is_refused();
[Fact] public void Fixtures_contain_no_secret_or_card_pattern();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test tests/OpenPayments.Psp.Tests --filter U5`.
- [ ] **Step 3: Implement.** Record new fixtures only against the sandbox, then scrub them.
- [ ] **Step 4: Run the sandbox tests,** skipped unless `STRIPE_SANDBOX=1` and keys are present: sale, authorize-capture, void, refund, replay of a timed-out call, callback delivery to a tunnel, settlement source availability. Record which ran and which were skipped in the progress line.
- [ ] **Step 5: Commit** `feat: add the Stripe sandbox adapter`.

### Task 33: One real 3D Secure flow (C39, E20)

**Files:**
- Create: `tests/OpenPayments.Psp.Tests/C39ThreeDSecureTests.cs`, `docs/phase-gates/c39-3ds.md`
- Modify: `src/OpenPayments.Psp/Stripe/StripeAdapter.cs` only as the test requires

- [ ] **Step 1: Write failing tests** against the sandbox, through the real API, reconciler and callback route, using Stripe's 3D Secure test payment-method tokens and Playwright on Stripe's test authentication page

```csharp
[Fact] public Task C39_finished_reaches_authorized_with_one_effect();
[Fact] public Task C39_failed_reaches_awaiting_method();
[Fact] public Task C39_abandoned_reaches_awaiting_method_through_unknown_and_recovery();   // E20, 2-min test window
[Fact] public Task C39_callback_racing_the_action_window_ends_in_one_correct_state();
```

- [ ] **Step 2: Run to verify they fail:** `STRIPE_SANDBOX=1 dotnet test tests/OpenPayments.Psp.Tests --filter C39`.
- [ ] **Step 3: Implement** what the flow needs. If it needs a new state, field or route in the contract, **stop and escalate**: that is the contract change D17 waits for.
- [ ] **Step 4: Run three times,** and write each run's date, commit and result in `docs/phase-gates/c39-3ds.md`.
- [ ] **Step 5: Commit** `test: prove one 3D Secure flow on the sandbox (C39)`.

### Task 34: Freeze the API contract (D17)

**Starts when** C39 has passed three times and the owner has said in chat that the contract may freeze.

**Files:**
- Modify: `api/openapi.yaml` (version `1.0.0`, title without "Draft"), `docs/decisions.md` (D17's status line only, by the design session that records the freeze), `README.md`
- Create: `tests/OpenPayments.Acceptance.Tests/ContractTests.cs`

- [ ] **Step 1: Write a failing test** that every route, status code and problem `code` the API serves appears in `api/openapi.yaml`, and the reverse, by walking the endpoint data source and the YAML.
- [ ] **Step 2: Run it** and fix any mismatch only in the direction the escalation answer gives.
- [ ] **Step 3: Lint:** `npx --yes @redocly/cli@latest lint api/openapi.yaml`.
- [ ] **Step 4: Commit** `docs: freeze the API contract after C39 (D17)`.

---

## Milestone J: Console review tooling (F2)

Build these as written, in this order: [console Task C5](2026-11-06-console.md#task-c5-applying-a-resolution-and-the-internal-route), then [console Task C12](2026-11-06-console.md#task-c12-needs-attention-and-review-resolution) with the [Needs attention](2026-11-09-needs-attention-ux.md) tasks N3 and N4 (N1 and N2 are built). Console Task C3 was built in Milestone G.

### Task 35: PSP events and last inquiry on the timeline (F2)

**Files:**
- Modify: `src/OpenPayments.Console/Data/TimelineReader.cs`, `src/OpenPayments.Console/Pages/Payments/Detail.cshtml`, `Detail.cshtml.cs`, `src/OpenPayments.Console/Presentation/StatePresentation.cs`
- Test: `tests/OpenPayments.Console.Tests/TimelinePspTests.cs`, `tests/OpenPayments.Console.Browser.Tests/BrowserTests.cs` (screen matrix)

**Interfaces:**
- `TimelineReader` reads `psp_events` by `payment_id` through `psp_events_payment`, and `last_inquiry_at`, `last_inquiry_result`, `recovery_attempts` from attempts and refunds, on the replica.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Timeline_shows_each_psp_event_with_source_and_outcome_oldest_first();
[Fact] public Task Contradiction_and_parked_events_are_marked_with_words_and_icon();
[Fact] public Task Unresolved_row_shows_last_inquiry_and_attempt_count();
[Fact] public Task Payload_text_renders_as_text_never_html();                     // console Review Focus 4
[Fact] public Task Timeline_query_uses_psp_events_payment_index();
```

- [ ] **Step 2: Run to verify they fail:** `dotnet test tests/OpenPayments.Console.Tests --filter TimelinePspTests`.
- [ ] **Step 3: Implement.**
- [ ] **Step 4: Verify** the console suites, including axe at 1280 and 390 px.
- [ ] **Step 5: Commit** `feat: show PSP events and the last inquiry on the payment timeline (F2)`.

---

## Milestone K: Stage benchmark and exit

### Task 36: Benchmarks: steps 0-5 with recovery, and G2

**Files:**
- Modify: `benchmarks/run.sh`, `benchmarks/profiles/steps.json` (mock profiles only, no gate values), `benchmarks/report.py`, `benchmarks/test_report.py`, `benchmarks/README.md`, `benchmarks/BASELINE.md`
- Create: `benchmarks/runs/<run-id>/*`, `docs/benchmarks/g2-per-account.md`

**Interfaces:**
- `run.sh <step> --stage phase2` sets the mock's `error_rate`, `approve_then_hang_rate`, `late_landing_rate`, `callback_rate` (1.0), `callback_duplicate_rate` and `callback_reorder_window_ms` from a `phase2` profile, starts the reconciler, waits for the backlog to drain before running checks, and runs `op-checks --stage phase2` (CK5 with timeouts, CK7-CK10, CK8 included).
- `result.md` adds callback req/s, parked events, recovery calls/s, keys expired/s and the expirable backlog, unresolved count at end, and commits per payment against [D47](../../decisions.md#d47)'s 4.2 with callbacks.

- [ ] **Step 1: Write failing report tests**

```python
def test_phase2_run_fails_if_unresolved_remain_after_drain(): ...
def test_phase2_run_fails_if_any_event_parked_past_limit(): ...
def test_recovery_calls_over_quota_invalidates_run(): ...
def test_phase2_is_labelled_preview(): ...
```

- [ ] **Step 2: Run to verify they fail:** `python3 -m pytest benchmarks/test_report.py -v`.
- [ ] **Step 3: Run steps 0-5 locally,** stopping at the first failed gate or invalid run, and record each in `BASELINE.md` as a preview with the bottleneck evidence ([E4](../../gates/stage-1-escalations.md#e4-task-17-has-no-linux-host-for-steps-4-5-escalation-e4) rules). Then run C23 and C47 at laptop scale, and say plainly that C23's "5k TPS" isn't reached. Keep the expiry job on throughout, with a prefilled expirable backlog; if live p99 rises with it or the backlog grows, record it as a bottleneck and escalate. That is when V7 runs ([D64](../../decisions.md#d64)).
- [ ] **Step 4: G2 per account.** For each sandbox account, record the documented sandbox and live limits, the limit `psp_accounts.rate_limit_per_s` uses, a 60-second sandbox run at 80% of the sandbox limit with no 429 beyond the test's own spike, and the arithmetic of how many merchant accounts the target needs. Write it in `docs/benchmarks/g2-per-account.md`. **G2 is answered "in writing" only by the PSP's written confirmation of live limits;** until then the file says so, and it is a [stop condition](../specs/2026-11-05-implementation-and-benchmark-design.md#stop-conditions) if the arithmetic can't reach the target.
- [ ] **Step 5: Commit** results with `test: record Stage 2 previews and G2 per account`.

### Task 37: C31b and the Phase 2 exit record

**Files:**
- Create: `tests/OpenPayments.Acceptance.Tests/C31bEdgeCaseTests.cs`, `docs/phase-gates/phase-2.md`
- Modify: `docs/verification.md` (progress counts only, for rows proved), `docs/gates/stage-2-progress.md`

- [ ] **Step 1: Write `C31b` as one test per edge case,** reusing earlier tests by name: E4 (Task 30), E9 (Task 25), E10 (Task 25), E19 (Task 25), E20 (Task 33), E22 and E23 (Task 31), E24 (Task 30). A test whose task was skipped (sandbox unavailable) fails rather than passes.
- [ ] **Step 2: Run the whole Phase 2 gate:** `dotnet build OpenPayments.slnx -warnaserror`, `dotnet test OpenPayments.slnx`, `dotnet list OpenPayments.slnx package --vulnerable --include-transitive`, the licence and secret scans, and `scripts/check-db-settings.sh`.
- [ ] **Step 3: Write `docs/phase-gates/phase-2.md`** with one row per Phase 2 ID and its evidence, and list by name anything not achieved. No checklist row in `verification.md` is ticked from a preview.
- [ ] **Step 4: Hand over to the review gate.** Every task has a progress line, no escalation is open, and the owner is told the build is ready for an independent review since the starting commit.
- [ ] **Step 5: Commit** `test: record the Phase 2 exit gate`.

---

## Verification mapping

Every Phase 2 ID from [Build phases](../../verification.md#build-phases), and where it's proved.

| ID | Task | Note |
|---|---|---|
| C3 | 27 | Authorize, capture and refund each |
| C4 | 27 | Crash harness |
| C9 | 25 | Acceptance and property permutations |
| C12 | 23 | |
| C13 | 23 | Failover between two mock PSPs only |
| C15 | 27 | Builds on Task 21 |
| C16 | 23 | Alert rules; DNS and TLS against the mock |
| C23 | 27, 36 | Laptop scale only; the "5k TPS" figure needs Linux |
| C26 | 27 | Two reconcilers, lapsed lease |
| C31b | 37 | E4, E9, E10, E19, E20, E22, E23, E24 from Tasks 25, 30, 31, 33 |
| C32 | 25 | Callback and API paths |
| C34 | 27 | With the reconciler in the race |
| C36 | 27 | Re-proved with the reconciler running |
| C37 | 27 | Re-proved with the reconciler running |
| C38 | 25, 27 | Late approval, automatic late void, late sale |
| C39 | 33 | Sandbox only |
| C40 | 31 | Mock; sandbox if Task 32 finds reports available |
| C41 | 30 | |
| C44 | 27 | Each recovery mode |
| C45 | 27 | Each recovery mode |
| C46 | 25, 26 | Crash, early event, parked retry |
| C47 | 27, 36 | |
| C50 | 28 | |
| C51 | 23 | |
| U5 | 23, 32 | Capabilities and recorded fixtures; event schema compatibility stays in Phase 3 |
| L7 | 21 | Stage 1 finding |
| L8 | 22 | Stage 1 finding |
| E3 checks | 20, 30 | `resolved_at` everywhere; expiry method by analysis ([D64](../../decisions.md#d64)) |
| G2 | 36 | In writing only from the PSP |

**Reverse, by task:**

| Task | Proves |
|---|---|
| 19 | Schema for the rest; no ID alone |
| 20 | E3 (`resolved_at`), part of C41 |
| 21 | L7, part of C15 and C46 |
| 22 | L8 |
| 23 | C12, C13, C16, C51, U5 (capabilities) |
| 24 | The mock that C9, C40, C44-C46 depend on |
| 25 | C9, C32, C38, C46, E9, E10, E19 |
| 26 | C46 (parked retry) |
| 27 | C3, C4, C15, C23, C26, C34, C36, C37, C38, C44, C45, C47 |
| 28 | C50 |
| Console C3 | Commands for Task 29 and C5 |
| 29 | D45 escalation |
| 30 | C41, E4, E24 |
| 31 | C40, E22, E23, CK8 |
| 32 | U5 (fixtures) |
| 33 | C39, E20 |
| 34 | D17 freeze |
| Console C5, C12, N3, N4, 35 | F2 |
| 36 | C23, C47 at stage scale, G2 |
| 37 | C31b, Phase 2 exit |

## What this stage does not include

- Outbox publishing, the broker, consumers and merchant webhooks (Stage 3). CK4 stays at CK4a; CK6 excludes outbox age.
- Any recorded result or ticked checklist row: everything is a preview until Phase 3 on Linux. `C`, `N` and the mock's 25,000 req/s check stay with the first Linux run ([D53](../../decisions.md#d53)).
- Live cards, live keys, or a production PSP account.
- A second real PSP, and failover between real PSPs.
- Merchant onboarding of PSP accounts: rows are seeded.
- Fee and adjustment ledger treatment, `SETTLED`, payouts and disputes.
- Client-side access and the reference checkout (Stage 4), test mode isolation (Stage 5).
- Webhook management and webhook visibility in the console (F3).

## Stop conditions and escalation

Per the [build gate](../../delivery-workflow.md#build-gate), stop that task, add an entry to `docs/gates/stage-2-escalations.md` with the task, the conflict, the evidence and the options seen, and go on only with tasks that don't depend on it, when:

- A requirement conflicts with another or with the design, or a decision this plan relies on is missing from the gate record.
- An interface doesn't match its task, or a test can't pass without changing what it asserts.
- A query-count ceiling ([D48](../../decisions.md#d48)) or a timer value would need to change.
- The PSP needs a new contract state, field or route (Task 33), or its facts differ from the gate record.
- A [stop condition](../specs/2026-11-05-implementation-and-benchmark-design.md#stop-conditions) occurs: G2 can't reach the target, or any run shows a duplicate charge, lost record or imbalance that can't be explained. Correctness work then comes before all load work.
- D56's laptop verification finds a defect in the payment path.
