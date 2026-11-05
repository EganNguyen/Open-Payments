# Open Payments Implementation and Benchmark Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build Open Payments in C#/.NET 10 and prove by measurement, with correctness checks after every run, whether it sustains 5,000 completed payments/s.

**Architecture:** Measure before building: a PostgreSQL spike answers gates G1, G3 and G4 on the real transaction shape. Then build the test tools (Phase 0), then the payment core around a pure `Decide` function (Phase 1), and run the first ramp steps. Later phases each get their own plan once the earlier numbers exist, because those numbers decide their schema and sizing.

**Tech Stack:** .NET 10, ASP.NET Core, Npgsql, Dapper, PostgreSQL 18, PgBouncer, pgbench, k6, xUnit, FsCheck, Testcontainers, Prometheus, Grafana, OpenTelemetry. Redpanda from Phase 3.

**Spec:** [Implementation and benchmark design](../specs/2026-11-05-implementation-and-benchmark-design.md), which sets scope, the definition of done and the benchmark rules. The system itself is defined in the design docs in `docs/`, chiefly [architecture](../../architecture.md), [payment lifecycle](../../payment-lifecycle.md), [verification](../../verification.md), [scaling playbook](../../scaling-playbook.md), [engineering standards](../../engineering-standards.md), [decisions](../../decisions.md) and [Stripe research](../../stripe-research.md). When this plan and a doc disagree, the doc wins unless Task 1 has changed it.

**Scope of this plan:** Tasks 1-17 are detailed, up to the first recorded ramp steps. The [roadmap](#roadmap-after-task-17) covers everything after that. It is enough to size and order the work, and each stage gets a detailed plan when it starts.

## Owner decisions needed

| # | Decision | Blocks | Recommendation |
|---|---|---|---|
| A | Accept the Task 1 design changes: accounts and fund flows, pure `Decide`, CK7-CK10, canonical log line, corrected phase gates | Task 2 onward | Accept. The Stripe research says the accounts and lifecycle choices must precede the first migration |
| B | Where the benchmarks that count will run | Task 6 answers, Task 17 onward | Rent Linux machines by the hour, only during runs. A laptop can't answer G1 ([playbook](../../scaling-playbook.md#workload-and-load-generation)), and no cloud CLI is installed here |
| C | Which PSP sandbox to use | Stage 2 (C39, G2), and freezing the API | Choose before Stage 2 starts. It must support idempotency keys, inquiry by our ID and 3D Secure in its sandbox. You sign up, not an agent |
| D | Auto-capture as a request field or a merchant setting ([lifecycle question 3](../../payment-lifecycle.md#open-questions)) | Task 2 | Request field `capture_mode` (`auto` or `manual`), since the benchmark mix needs both |

## Problems found in the docs

Task 1 resolves these. The arithmetic is ours and unmeasured, so each figure is a reason to test, not a result.

| # | Problem | Effect | Resolution |
|---|---|---|---|
| 1 | The docs estimate 8-10 writes per payment. Counting the two-transaction flow gives about 20-22 row writes and up to 4 commits per auto-captured payment | About 100-110k row writes/s and up to 20k commits/s at target, not the stated 40-50k writes/s. Refunds add about 1.7k commits/s. G1 could be judged against the wrong number | Spike the real transaction shape. Correct the derived load |
| 2 | A pool of about 100 connections at 20k tx/s allows only 5 ms per transaction by Little's Law, including the synchronous commit | The pool may saturate before the database does. PgBouncer is single-threaded, so one process may also limit | Spike varies pool size, PgBouncer process count and statement pipelining |
| 3 | Gates require CK1-CK6 at every ramp step, but steps 0-7 have no broker and Phase 1 has no reconciler | CK4 and CK5 can't pass at those steps as written | Define which checks apply at each stage, below |
| 4 | Phase 1 exit includes all of C31, but E10, E19, E20 and E22-E24 need Phase 2 parts, and E14 and E15 need Phase 3 events. E8 needs void, which is `Proposed` | Phase 1 can never exit | Split C31 by phase |
| 5 | L1-L6 exit in Phase 4, but fraud checks, the vault and test mode are in Phase 5. Edge gateway and vault appear in no phase by name | The headline number would come from a path missing a 50 ms synchronous step and a shared dependency | Build minimal synchronous fraud, vault and edge in Stage 4. Re-run L5 after Stage 5 as the sign-off number |
| 6 | The one-live-attempt unique indexes must include the partition key if `payment_attempts` is partitioned, but `settlement_lines` has a foreign key on `attempt_id` alone | The table can't be partitioned on anything but `payment_id`, and then `attempt_id` is no longer unique by itself | Candidate: partition by `payment_id`, primary key `(payment_id, attempt_id)`, carry `payment_id` on referencing rows. Decided in Task 6 |
| 7 | At the example mix, refunds run at about 830/s. The per-account advisory lock is held through a synchronous commit | At 2 ms held, one merchant tops out near 500 refunds/s, so a skewed mix may hit this first | Spike variant V8, and a skewed merchant mix in every run |
| 8 | API keys and merchants have no table, and `Merchant profiles` is `Open` | Authentication and D20 can't be built | Minimal `api_keys` table in Task 1. Full merchant platform stays out of scope |

**Which checks apply when:**

| Stage | CK1-CK3 | CK4 | CK5 | CK6 | CK7-CK10 |
|---|---|---|---|---|---|
| Spike | CK1, CK3 on the SQL | n/a | n/a | n/a | n/a |
| After Phase 1 | Yes | CK4a only: every transition has its outbox row | Yes, with no PSP timeouts injected | Outbox age excluded | Completeness and cross-check only |
| After Phase 2 | Yes | CK4a | Yes, with timeouts | Outbox age excluded | Adds clearing, timeliness |
| After Phase 3 onward | Yes | Full | Yes | Full | Yes |

A run from before Phase 3 can't satisfy L1. It's recorded as a *preview*, and no [checklist row](../../verification.md#production-problem-checklist) is ticked from it.

## Global Constraints

- Commit and push directly to `main`, staging explicit paths.
- Commit author: `git commit --author="EganNguyen <tuannh2y@gmail.com>"`. Every message ends with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- Target `net10.0`. Warnings are errors. Analyzer CA2016 is on.
- Money is `bigint` minor units with a currency. No `double`, `float` or `decimal` for amounts.
- Only `OpenPayments.Transitions` writes money state ([D2](../../decisions.md#d2)). Hot path uses Dapper and parameterized SQL. No EF Core in Phases 0-1.
- No PSP, HTTP or broker call inside a transaction. At most about 5 statements per transaction.
- Database settings: `deadlock_timeout` 100 ms; role `lock_timeout` 200 ms, `statement_timeout` 1 s, `idle_in_transaction_session_timeout` 2 s. Retry only `40001` and `40P01`, at most twice with jitter. `55P03` returns 503 with `Retry-After`.
- Request budget 5 s: fraud 50 ms, PSP 2.5 s, each database call 200 ms. Body limit 64 KB. In-flight cap 500 per instance, with no queue.
- Idempotency: unique `(merchant_id, key)`; same hash returns stored result; different hash 422; in flight 409 with `Retry-After`. Retention at least 24 h after resolve, never while unresolved.
- Attempt cap: 10 authorize attempts per payment.
- Load generation uses an open model (`ramping-arrival-rate`; `pgbench -R` in the spike). Fixed PSP latency for comparable runs.
- Check every dependency's license and .NET 10 support before adding it (S6). Do not add FluentAssertions, MediatR or MassTransit, whose recent versions are commercially licensed; recheck at adoption.
- `OpenPayments.Checks` must not reference `Domain` or `Transitions`, or it shares their bugs.
- Tick a verification row only with a recorded run ID, commit, configuration and result link. All numbers stay estimates until measured.
- Test names use the doc's ID where one exists, such as `C5_...`, so proof is searchable.

## Review Focus

Likely to bite and not named by any single doc test. Each has a test in the task shown.

1. **A benchmark whose generator or mock PSP saturates first.** It must be marked invalid, not reported as a system result (Tasks 8, 11).
2. **Client disconnect between transaction A and B.** The request token cancels, but the PSP call's outcome and transaction B must still complete (Task 14).
3. **A correctness check that passes on empty or missing data.** Each CK must fail on injected corruption and on zero rows (Task 9).
4. **Amounts at the limits:** JSON numbers above 2^53, sums overflowing `long`, and zero-decimal and three-decimal currencies (Tasks 7, 14).
5. **Transaction B fails after the PSP approved.** The client must never see a decline, retries must not call the PSP under a new key, and the payment must stay findable (Task 14).

---

## Milestone A: Design closure

### Task 1: Record design decisions and correct the docs

**Files:**
- Modify: `docs/decisions.md`, `docs/architecture.md`, `docs/payment-lifecycle.md`, `docs/verification.md`, `docs/scaling-playbook.md`, `docs/stripe-research.md`, `README.md`

**Interfaces:**
- Produces: the DDL and rules that Tasks 4, 6, 9 and 12 implement.

- [ ] **Step 1: Add decision records** after owner approval (decision A), following the existing format:
  - **D21 Accounts and fund flows.** Adopt the fund-flow table in [Stripe research lesson 1](../../stripe-research.md#1-balanced-is-not-the-same-as-correct). Authorization entries are logical. A transaction may not mix `real` and `logical`. Fee and adjustment treatment stays `Open`, and benchmarks post 2-4 entries per capture to cover it.
  - **D22 Pure decision function.** `Decide` has no database, clock, random or network access. Time and IDs are inputs. Amends [D2](../../decisions.md#d2).
  - **D23 Independent checks and the canonical log line.** Add CK7 clearing, CK8 timeliness, CK9 completeness and CK10 cross-check, with one wide log line per request.
  - **D24 Corrected gates.** Everything in [Problems found](#problems-found-in-the-docs) rows 1, 3, 4 and 5.
- [ ] **Step 2: Add the specified tables to the architecture data model**

```sql
create table currencies (
  code char(3) primary key, exponent smallint not null, rounding text not null);
create table accounts (
  id uuid primary key,
  merchant_id uuid,                -- null for platform accounts
  account_type text not null,
  sub_account smallint not null default 0 check (sub_account between 0 and 63),
  class text not null check (class in ('clearing','terminal')),
  class_of_money text not null check (class_of_money in ('real','logical')),
  currency char(3) not null references currencies(code),
  unique nulls not distinct (merchant_id, account_type, sub_account, currency));
create table api_keys (
  key_hash bytea primary key,      -- SHA-256; never store the key itself
  merchant_id uuid not null,
  mode text not null check (mode in ('live','test')),
  scopes text[] not null,
  revoked_at timestamptz);
-- ledger_transactions gains: effective_at timestamptz not null
-- ledger_entries gains:      subject_id uuid not null
```

- [ ] **Step 3: Correct the lifecycle ledger effects.** Replace "debit customer funds" with `psp_receivable` and `merchant_payable`, and point authorization at `auth_hold`.
- [ ] **Step 4: Correct the verification and playbook docs.** Add CK7-CK10. Split C31 into C31a (E1-E9, E11-E13, E16-E18, E21) in Phase 1, C31b (E10, E19, E20, E22-E24) in Phase 2 and C31c (E14, E15) in Phase 3, with E8 limited to rejected capture from a non-`AUTHORIZED` state. Add the applicability table. Add the edge gateway and vault to Phase 4, and require L5 again after Phase 5.
- [ ] **Step 5: Verify nothing was lost**

Run: `grep -c '| ☐ |' docs/verification.md`
Expected: `133` (108 problems plus 25 readiness rows, unchanged).

- [ ] **Step 6: Commit** with message `docs: record D21-D24 and correct phase gates`.

### Task 2: Draft the API contract and event catalog

**Files:**
- Create: `api/openapi.yaml`, `docs/event-catalog.md`, `docs/integration-guides-draft.md`
- Modify: `README.md` (document table)

**Interfaces:**
- Produces: routes and JSON field names that Tasks 11, 14, 15 and 16 use.

- [ ] **Step 1: Write `api/openapi.yaml`, version `0.x`, titled "Draft until C39 passes" ([D17](../../decisions.md#d17))**

| Route | Purpose | Answers |
|---|---|---|
| `POST /v1/payments` | Create, with or without a method | 200, 202, 402 (decline), 409, 422, 429, 503 |
| `POST /v1/payments/{id}/attempts` | New attempt | Same |
| `POST /v1/payments/{id}/cancel` | Cancel | 200, 409 |
| `POST /v1/payments/{id}/capture` | Capture | 200, 202, 409, 422 |
| `POST /v1/payments/{id}/refunds` | Refund | 200, 202, 409, 422 |
| `GET /v1/payments/{id}`, `GET /v1/refunds/{id}` | Status | 200, 404 |

Request fields are `amount_minor` (integer), `currency`, `method: {type, ref, details}`, `capture_mode`, `error_on_action`. Mutations require an `Idempotency-Key` header. Errors use `application/problem+json` with a stable `code`.

- [ ] **Step 2: Test the shape on paper.** Add `docs/integration-guides-draft.md` with short walk-throughs for card, 3D Secure, bank debit and voucher. If any needs a new state machine, change the contract now.
- [ ] **Step 3: Write `docs/event-catalog.md`** from [Merchant events](../../payment-lifecycle.md#merchant-events), with `event_id`, per-payment `sequence`, `schema_version` and the 32 KB limit. Recommend thin or full events for the owner to confirm.
- [ ] **Step 4: Validate**

Run: `npx --yes @redocly/cli@latest lint api/openapi.yaml`
Expected: no errors.

- [ ] **Step 5: Commit** with message `docs: draft API contract and event catalog`.

---

## Milestone B: Database spike (gates G1, G3, G4)

This is the README's first step. It needs no C#.

### Task 3: Tooling and replicated database stack

**Files:**
- Create: `deploy/compose/compose.yaml`, `deploy/compose/postgres/primary.conf`, `deploy/compose/pgbouncer/pgbouncer.ini`, `scripts/check-db-settings.sh`, `scripts/bootstrap.sh`

**Interfaces:**
- Produces: `DB_URL` (through PgBouncer) and `DB_DIRECT_URL` (primary).

- [ ] **Step 1: Ask the user to start Docker Desktop.** It is installed but not running, and an agent shouldn't change system services.
- [ ] **Step 2: Write `scripts/bootstrap.sh`,** which checks for and prints install commands for the .NET 10 SDK, k6 and pytest rather than installing silently.
- [ ] **Step 3: Write `scripts/check-db-settings.sh`,** which exits non-zero unless all of these hold:
  - `synchronous_standby_names` is `ANY 1 (standby1, standby2)`.
  - `pg_stat_replication` shows 2 rows with `sync_state = 'quorum'`.
  - `deadlock_timeout` is 100 ms, and the application role has the [Global Constraints](#global-constraints) timeouts.
- [ ] **Step 4: Run it to verify it fails** against no stack.

Run: `scripts/check-db-settings.sh`
Expected: non-zero exit with a connection error.

- [ ] **Step 5: Write `compose.yaml`:** PostgreSQL 18 primary, two streaming standbys, PgBouncer in transaction mode with pool 100.
- [ ] **Step 6: Verify the stack**

Run: `docker compose -f deploy/compose/compose.yaml up -d --wait && scripts/check-db-settings.sh`
Expected: exit 0.

- [ ] **Step 7: Commit** with message `build: add replicated PostgreSQL stack and settings check`.

### Task 4: Spike schema and transaction scripts

**Files:**
- Create: `db/spike/schema-a-hash.sql`, `db/spike/schema-b-range.sql`, `db/spike/seed.sql`, `db/spike/tx-auth-capture.pgbench`, `db/spike/tx-decline.pgbench`, `db/spike/tx-refund.pgbench`, `db/spike/verify.sql`

**Interfaces:**
- Consumes: the Task 1 DDL and the `DB_URL` from Task 3.
- Produces: scripts that Task 5 runs, using `\set merchant random_zipfian(...)`.

- [ ] **Step 1: Write `verify.sql`,** which raises an exception if any transaction is unbalanced, any `refunded + pending > captured`, any `CAPTURED` payment lacks a capture ledger transaction, or there are zero payments.
- [ ] **Step 2: Run it to verify it fails** on an empty schema.

Run: `psql "$DB_DIRECT_URL" -f db/spike/schema-a-hash.sql && psql "$DB_DIRECT_URL" -v ON_ERROR_STOP=1 -f db/spike/verify.sql`
Expected: error "zero payments".

- [ ] **Step 3: Write two schemas.** A follows the current docs with the Task 1 additions. B range-partitions `payments` and `ledger_entries` on UUIDv7 keys and hash-partitions `payment_attempts` by `payment_id`, per [problem 6](#problems-found-in-the-docs). Both include the deferred balance trigger and every documented partial index.
- [ ] **Step 4: Write transaction scripts matching the real flow,** one pgbench transaction per database transaction:
  - `tx-auth-capture`: A (idempotency, payment, attempt), B (payment, attempt, idempotency, ledger transaction, 2 entries, outbox), capture A, capture B (a hold-release ledger transaction with 2 entries, and a money ledger transaction with 2-4 entries, per D21).
  - `tx-decline`: A, then B moving the payment back with no ledger rows.
  - `tx-refund`: advisory lock and balance read, reserve, ledger reservation, then confirm.
- [ ] **Step 5: Write `seed.sql`,** with parameters for merchant count (1, 100, 10,000), an 80/20 skew and prefill size. Target for Linux runs: 100M payments with matching children. Benchmarking an empty database would flatter every number.
- [ ] **Step 6: Run a short check**

Run: `pgbench "$DB_URL" -n -f db/spike/tx-auth-capture.pgbench -R 50 -T 30 && psql "$DB_DIRECT_URL" -v ON_ERROR_STOP=1 -f db/spike/verify.sql`
Expected: both exit 0.

- [ ] **Step 7: Commit** with message `test: add database spike schemas and workloads`.

### Task 5: Spike runner

**Files:**
- Create: `db/spike/run-matrix.sh`, `db/spike/matrix.yaml`, `db/spike/report.py`, `db/spike/test_report.py`, `benchmarks/spike/README.md`

**Interfaces:**
- Produces: `benchmarks/spike/<run-id>/result.json` with `variant`, `target_tps`, `achieved_tps`, `latency_p50_ms`, `latency_p99_ms`, `primary_cpu_pct`, `wal_mb_s`, `commit_ms`, `deadlocks`, `verify_passed`, `valid`, and `benchmarks/spike/<run-id>/summary.md`.

- [ ] **Step 1: Write failing tests for `report.py`**

```python
def test_highest_rate_requires_cpu_gate():
    # 12k at 55% and 15k at 72%: answer is 12k, since 60% is the limit
    assert sustained_rate(rows(12000, 55, 15000, 72)) == 12000

def test_failed_verify_disqualifies_row():
    assert sustained_rate(rows(20000, 40, verify_passed=False)) == 0

def test_required_commits_from_mix():
    # 5,000 payments, 4 commits each, 833 refunds at 2 commits each
    assert required_commits(5000, 4, 833, 2) == 21666
```

- [ ] **Step 2: Run to verify they fail**

Run: `python3 -m pytest db/spike/test_report.py -v`
Expected: FAIL on missing `sustained_rate`.

- [ ] **Step 3: Implement `run-matrix.sh` and `report.py`.** Run each variant at rising `pgbench -R` rates, with a 2-minute warm-up and 10 minutes measured. Sample `pg_stat_database`, `pg_stat_wal`, `pg_stat_replication` and CPU every 10 s. Mark a row invalid if the generator exceeds 80% CPU. Run `verify.sql` after each row.
- [ ] **Step 4: Define the matrix.** Change one thing at a time from the baseline (schema A, sync on, trigger on, RLS off, pool 100, one PgBouncer, 100 merchants):

| Variant | Change | Answers |
|---|---|---|
| V1 | Synchronous replication off | G3: cost of quorum commit |
| V2 | Balance trigger off | G4 |
| V3 | RLS on with `SET LOCAL` | Cost of the tenant backstop |
| V4 | Pool 50, 100, 200, 400; PgBouncer 1 and 4 processes | Problem 2, connection budget |
| V5 | Schema B | Partition choice |
| V6 | Pipelined statements (`\startpipeline`) | Round trips per transaction |
| V7 | Batched idempotency-key deletes running throughout | D19 expiry |
| V8 | Refund mix with 1, 100 and 10,000 merchants | D9 lock limit, problem 7 |

- [ ] **Step 5: Run tests to verify they pass, then dry-run** the matrix for 60 s per row on the laptop.

Run: `python3 -m pytest db/spike/test_report.py -v && MEASURE_SECONDS=60 db/spike/run-matrix.sh`
Expected: PASS, then a `summary.md` headed "Local: comparative only, does not answer G1".

- [ ] **Step 6: Commit** with message `test: add spike matrix runner and report`.

### Task 6: Run the spike and decide

Needs decision B. Machines: primary and two standbys on NVMe with WAL on its own volume in three zones, plus a separate generator. Start with 16-32 vCPU, record what was used, and shut them down after.

**Files:**
- Create: `benchmarks/spike/<run-id>/*`, `benchmarks/spike/REPORT.md`, `db/migrations/0001_initial.sql`, `scripts/migrate.sh`
- Modify: `docs/decisions.md`, `docs/scaling-playbook.md`, `docs/architecture.md`

**Interfaces:**
- Produces: `db/migrations/0001_initial.sql`, the schema every later task uses, and the D25 partitioning and D26 G1 records.

- [ ] **Step 1: Run the full matrix on Linux** with a 100M prefill. Check `valid` on every row.
- [ ] **Step 2: Write `REPORT.md`:** required commits/s, sustained commits/s at 60% CPU or less for each variant, the first bottleneck and what's left unmeasured.
- [ ] **Step 3: Bring the answers to the owner.** A one-cell or cells recommendation for G1 is their call under [D14](../../decisions.md#d14). G3 and G4 get provisional answers, since only full-system runs can close them.
- [ ] **Step 4: Record D25 (partitioning and retention) and D26 (G1).** If G1 requires cells, stop and replan the roadmap before continuing.
- [ ] **Step 5: Write `0001_initial.sql` from the chosen variant.** Apply it twice to prove the runner's recorded-version check, with `scripts/migrate.sh` using a `schema_migrations` table.

Run: `scripts/migrate.sh && scripts/migrate.sh && psql "$DB_DIRECT_URL" -c "select count(*) from schema_migrations"`
Expected: both runs exit 0, count 1.

- [ ] **Step 6: Commit** with message `feat: add initial schema from spike results`.

---

## Milestone C: Phase 0, test tools

### Task 7: Solution skeleton, static gates and money type

**Files:**
- Create: `OpenPayments.slnx`, `global.json`, `Directory.Build.props`, `Directory.Packages.props`, `BannedSymbols.txt`, `.github/workflows/ci.yml`
- Create: `src/OpenPayments.Domain/Money.cs`, `src/OpenPayments.Domain/Ids.cs`
- Test: `tests/OpenPayments.Domain.Tests/MoneyTests.cs`, `tests/OpenPayments.Analyzers.Tests/BannedApiTests.cs`

**Interfaces:**
- Produces: `readonly record struct Money(long Minor, CurrencyCode Currency)`; `Money.Add(Money) -> Money`; `Money.ParseMinor(JsonElement, CurrencyCode) -> Result<Money>`; `AttemptId`, `PaymentId`, `RefundId`, `MerchantId` as UUIDv7-backed value types.

- [ ] **Step 1: Write failing tests (U1)**

```csharp
[Fact] public void S1_build_fails_on_task_result(); // compiling a file using .Result under request-path rules reports RS0030
[Property] public bool U1_add_is_commutative_same_currency(long a, long b);
[Fact] public void U1_add_mixed_currency_throws();
[Fact] public void U1_add_overflow_throws_never_wraps();          // long.MaxValue + 1
[Theory] [InlineData("JPY", 0)] [InlineData("USD", 2)] [InlineData("KWD", 3)]
public void U1_exponent_per_currency(string code, int exponent);
[Theory] [InlineData("0")] [InlineData("-1")] [InlineData("1.5")] [InlineData("9007199254740993")] [InlineData("1e3")]
public void E11_rejected_or_exact(string json); // never rounded through a double
```

`9007199254740993` is above 2^53: parse it exactly and then apply the configured maximum amount, never through `double`.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "U1|E11|S1"`
Expected: FAIL to compile, since `Money` is missing.

- [ ] **Step 3: Implement.** `Directory.Build.props` sets warnings as errors, nullable, CA2000 and CA2016. `BannedSymbols.txt` lists `Task.Result`, `Task.Wait`, `TaskAwaiter.GetResult`, `Task.Run`, `Thread..ctor`, and sync `NpgsqlConnection.Open` and `HttpClient.Send`, enforced by `Microsoft.CodeAnalysis.BannedApiAnalyzers` in request-path projects. CI runs build, tests and a license and vulnerability scan (S6).
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test`
Expected: PASS.

- [ ] **Step 5: Commit** with message `feat: add solution skeleton, static gates and money type`.

### Task 8: Mock PSP

**Files:**
- Create: `tools/OpenPayments.MockPsp/Program.cs`, `Outcomes.cs`, `EffectLog.cs`, `AttemptStore.cs`
- Test: `tests/OpenPayments.MockPsp.Tests/MockPspTests.cs`

**Interfaces:**
- Produces, over HTTP:
  - `POST /authorize`, `/capture`, `/refund` with an `Idempotency-Key` header, returning `{outcome, psp_reference, amount_minor, currency, failure_code?, action?}`.
  - `GET /inquire/{key}`, returning `{status: approved|declined|action_required|unknown_attempt|evicted, ...}`.
  - `PUT /admin/config` with `{latency_ms, jitter_ms, decline_rate, error_rate, approve_then_hang_rate, rate_limit_per_s, action_rate, mismatch_rate, seed}`.
  - `GET /admin/stats`. An effect log at `$EFFECTS_DIR/effects.csv` with columns `key,op,amount_minor,currency,at`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Same_key_returns_stored_answer_and_one_effect();
[Fact] public Task Hundred_concurrent_same_key_one_effect();
[Fact] public Task Approve_then_hang_records_effect_then_never_answers(); // inquire says approved
[Fact] public Task Inquire_unseen_key_returns_unknown_attempt();
[Fact] public Task Evicted_key_returns_evicted_never_unknown_attempt();
[Fact] public Task Same_seed_gives_same_outcomes();
[Fact] public Task Over_rate_limit_returns_429_with_no_effect();
```

`evicted` must differ from `unknown_attempt`. The reconciler treats `unknown_attempt` as "never charged" and fails the attempt, so an evicted key reported that way would corrupt a soak test.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.MockPsp.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement.** Outcome is `hash(key, seed)` mapped through the configured rates. Delay uses `Task.Delay`. The store holds a time-bounded window (default 2 h), since a 24 h soak at target means over a billion records. The effect log is written through a bounded channel before the response returns. Effects stay on disk for CK2.
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OpenPayments.MockPsp.Tests`
Expected: PASS.

- [ ] **Step 5: Prove the mock isn't the limit.** With k6 at 25,000 req/s (three times the target's 8,300) and 200 ms latency, added latency p99 is under 5 ms. Record it in `benchmarks/mock-psp-capacity.md`. If it falls short, run one instance per shard by key hash. This must be run on benchmark-class hardware before any Linux run is trusted. On the laptop, record what was reached.
- [ ] **Step 6: Commit** with message `feat: add mock PSP with effect log`.

### Task 9: Correctness check runner

**Files:**
- Create: `tools/OpenPayments.Checks/Program.cs`, `tools/OpenPayments.Checks/sql/ck1.sql` through `ck10.sql`, `tools/OpenPayments.Checks/Ck6Trend.cs`
- Test: `tests/OpenPayments.Checks.Tests/CorruptionTests.cs`, `tests/OpenPayments.Checks.Tests/Fixture.cs`

**Interfaces:**
- Consumes: `0001_initial.sql` and the effect log from Task 8.
- Produces: CLI `op-checks --db <url> --effects <path> --stage <phase0|phase1|phase2|phase3> [--prom <url> --from <t> --to <t>] --out <file.json>`. Output `{check, applicable, passed, rows_examined, violations, sample_ids[], money_impact_minor}`. Exit 1 on any failure.

- [ ] **Step 1: Write failing tests.** The fixture inserts rows with raw SQL, never through `Transitions`. Each corruption gets its own test:

```csharp
[Fact] public Task CK1_detects_unbalanced_txn();
[Fact] public Task CK1_detects_updated_or_deleted_ledger_row(); // via row-count and checksum manifest
[Fact] public Task CK2_detects_two_effects_for_one_key();
[Fact] public Task CK2_detects_succeeded_attempt_without_effect();
[Fact] public Task CK2_detects_effect_without_attempt();
[Fact] public Task CK3_detects_pending_not_matching_unresolved_refunds();
[Fact] public Task CK4a_detects_transition_without_outbox_row();
[Fact] public Task CK5_detects_stale_unresolved_operation();
[Fact] public Task CK9_detects_captured_payment_without_capture_txn();
[Fact] public Task CK10_detects_captured_minor_disagreeing_with_ledger();
[Fact] public Task Every_check_fails_on_zero_rows();
[Fact] public Task Inapplicable_check_reports_not_applicable_never_passed();
[Fact] public Task Clean_fixture_passes_all_applicable();
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Checks.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement.** SQL is set-based for full scans over hundreds of millions of rows, and runs on a standby where possible. Load effects with `COPY` into an unlogged `ck.psp_effects` table in a separate schema. Apply the [applicability table](#problems-found-in-the-docs) by `--stage`.
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OpenPayments.Checks.Tests`
Expected: PASS.

- [ ] **Step 5: Commit** with message `feat: add independent correctness checks`.

### Task 10: Observability stack

**Files:**
- Create: `deploy/compose/observability.yaml`, `deploy/compose/prometheus/prometheus.yml`, `deploy/compose/grafana/dashboards/overview.json`, `docs/metrics.md`

**Interfaces:**
- Produces the metric names Task 14 emits and Task 11 reads: `op_requests_admitted_total`, `op_requests_rejected_total{reason}`, `op_payments_completed_total{outcome}`, `op_platform_overhead_seconds` (buckets from 1 ms to 1 s, with several between 25 and 100 ms), `op_psp_wait_seconds`, `op_inflight`, `op_tx_retries_total{sqlstate}`, `op_conditional_update_zero_rows_total`, `op_outbox_oldest_age_seconds`, `op_payments_by_state`.

- [ ] **Step 1: Add Prometheus, Grafana, postgres-exporter and PgBouncer exporter.** Dashboard panels cover the [Results log](../../scaling-playbook.md#results-log) fields.
- [ ] **Step 2: Verify**

Run: `docker compose -f deploy/compose/observability.yaml up -d --wait && curl -sf localhost:9090/-/ready`
Expected: exit 0.

- [ ] **Step 3: Commit** with message `build: add metrics stack and dashboard`.

### Task 11: Load harness with run validity

**Files:**
- Create: `benchmarks/k6/mix.js`, `benchmarks/k6/lib/keys.js`, `benchmarks/profiles/steps.json`, `benchmarks/seed-refundable.sql`, `benchmarks/run.sh`, `benchmarks/report.py`, `benchmarks/test_report.py`, `benchmarks/README.md`

**Interfaces:**
- Consumes: routes from Task 2, metrics from Task 10, `op-checks` from Task 9, the mock admin API from Task 8.
- Produces: `benchmarks/run.sh <step> --env local|linux --stage <stage> [--psp-latency 200]`, writing `benchmarks/runs/<run-id>/{config.json,k6-summary.json,checks.json,result.md}`.

- [ ] **Step 1: Write failing tests for `report.py`**

```python
def test_dropped_iterations_invalidates_run():
    assert validity(k6(dropped=1), gen_cpu=40).valid is False

def test_generator_cpu_over_80_invalidates_run():
    assert validity(k6(), gen_cpu=81).valid is False

def test_mock_added_latency_invalidates_run():
    assert validity(k6(), gen_cpu=40, mock_added_p99_ms=20).valid is False

def test_failed_ck_fails_gates_even_if_fast():
    assert gates(p99=10, errors=0, checks=[ck("CK1", False)]).passed is False

def test_not_applicable_ck_is_shown_not_passed():
    assert "n/a" in render(ck("CK4", None))

def test_counter_must_match_database():
    # completed metric 1000, database shows 990: run fails on a measurement error
    assert reconcile_counts(metric=1000, db=990).ok is False

def test_preview_label_before_phase3():
    assert "preview" in render_header(stage="phase1")
```

- [ ] **Step 2: Run to verify they fail**

Run: `python3 -m pytest benchmarks/test_report.py -v`
Expected: FAIL on missing `validity`.

- [ ] **Step 3: Write `mix.js`.** Use one `ramping-arrival-rate` scenario per operation at the [example mix](../../scaling-playbook.md#workload-and-load-generation): 60% create and pay, 15% status, 10% refund, 10% retry with a recent key, 5% other. Merchants come from an 80/20 skewed list. Retried keys come from a per-VU ring buffer, and refund targets from a seeded `SharedArray`, so generators need no shared state. Vendor any k6 helper locally rather than importing from a URL at run time. `steps.json` holds steps 0-11 from the [ramp plan](../../scaling-playbook.md#ramp-plan). 429 and 503 are counted separately from errors.
- [ ] **Step 4: Write `run.sh`.** Record commit and configuration, reset to the prefilled snapshot, set the mock, run k6, wait for the drain, run `op-checks`, then fill the Results log into `result.md`. Refuse to run on a dirty git tree, ignoring `benchmarks/runs/`.
- [ ] **Step 5: Run to verify they pass**

Run: `python3 -m pytest benchmarks/test_report.py -v`
Expected: PASS.

- [ ] **Step 6: Verify the Phase 0 exit** by running the harness against the mock PSP directly, using a route override. No API exists yet, so `op-checks` must report failures on zero rows rather than pass.

Run: `TARGET_OVERRIDE=mock benchmarks/run.sh 0 --env local --stage phase0`
Expected: a run directory with `checks.json` showing failures for no data, and non-zero exit. This shows checks run automatically and can't pass vacuously.

- [ ] **Step 7: Commit** with message `feat: add load harness with run validity rules`.

---

## Milestone D: Phase 1, payment core

### Task 12: Pure state machine and ledger postings

**Files:**
- Create: `src/OpenPayments.Domain/Decide.cs`, `States.cs`, `Commands.cs`, `Postings.cs`
- Test: `tests/OpenPayments.Domain.Tests/DecideTransitionTests.cs`, `StateSearchTests.cs`, `LedgerPropertyTests.cs`, `tests/OpenPayments.Domain.Tests/golden/*.json`

**Interfaces:**
- Produces:
```csharp
public static TransitionResult Decide(PaymentSnapshot current, PaymentCommand command, RuleVersion rules);
// TransitionResult is Accepted(NewState, AttemptChange?, RefundChange?, LedgerLine[], OutboxEvent[]),
// Rejected(ReasonCode) or NoOp(ReasonCode).
```
  `PaymentCommand` cases include the attempt, PSP-answer, timeout, inquiry, capture, refund-reserve, refund-confirm, refund-release, cancel and manual-resolution commands. All IDs and times are command fields.

- [ ] **Step 1: Write failing tests (U2, U3)**

```csharp
[Theory, MemberData(nameof(AllStatesAllCommands))]
public void U2_only_listed_transitions_are_accepted(PaymentState s, PaymentCommand c);
[Fact] public void U2_every_lifecycle_committed_row_is_covered();   // table parsed from a checked-in copy
[Fact] public void U2_exhaustive_search_holds_invariants_at_every_step();
[Property] public bool U3_every_posting_balances(PaymentCommand c);
[Property] public bool U3_no_transaction_mixes_real_and_logical(PaymentCommand c);
[Fact] public void U3_final_balance_matches_final_state();          // full worked example: all clearing 0
[Fact] public void Late_approval_on_failed_attempt_goes_to_review_with_no_ledger();
[Fact] public void Amount_mismatch_goes_to_review_with_no_ledger();
[Fact] public void Attempt_cap_rejects_eleventh();
[Fact] public void Decide_project_references_no_io(); // assembly-reference test
[Theory, MemberData(nameof(GoldenFiles))] public void Golden_cases_match(string file);
```

The search is breadth-first over state × commands to a bounded depth. Invariants: refunded + pending ≤ captured, at most one successful authorize and capture, balanced ledger, no transition out of a final state.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Domain.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement `Decide`** from the [transitions table](../../payment-lifecycle.md#transitions), `Committed` rows only, and postings from D21. Golden files come from the lifecycle's worked examples.
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OpenPayments.Domain.Tests`
Expected: PASS.

- [ ] **Step 5: Commit** with message `feat: add pure payment decision function`.

### Task 13: Transaction runner and store

**Files:**
- Create: `src/OpenPayments.Transitions/TxRunner.cs`, `PaymentStore.cs`, `Sql.cs`
- Test: `tests/OpenPayments.Integration.Tests/TxRunnerTests.cs`, `PaymentStoreTests.cs`, `DbFixture.cs`

**Interfaces:**
- Consumes: `Decide` from Task 12, `0001_initial.sql` from Task 6.
- Produces:
```csharp
Task<T> TxRunner.RunAsync<T>(MerchantId m, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task<T>> work, CancellationToken ct);
Task<BeginResult> PaymentStore.BeginAttemptAsync(BeginAttempt cmd, CancellationToken ct);
// Started(AttemptId) | Replay(StoredResponse) | KeyInFlight | HashMismatch | LiveAttemptExists(PaymentView) | CapExceeded | Rejected(ReasonCode)
Task<ApplyResult> PaymentStore.ApplyAsync(PaymentId p, PaymentCommand c, StoredResponse? r, CancellationToken ct);
// Applied(PaymentView) | AlreadyMoved(PaymentView) | Rejected(ReasonCode)
```

- [ ] **Step 1: Write failing tests against real PostgreSQL** (Testcontainers)

```csharp
[Fact] public Task Retries_40P01_at_most_twice_then_throws_retryable();
[Fact] public Task Does_not_retry_55P03();                    // maps to Busy (503)
[Fact] public Task Does_not_retry_unique_violation();
[Fact] public Task Sets_local_merchant_id_each_transaction();  // pooled connection, no leak between tenants
[Fact] public Task Apply_commits_state_ledger_outbox_or_nothing(); // failpoint on outbox insert
[Fact] public Task Zero_rows_update_returns_already_moved_never_overwrites();
[Fact] public Task Begin_duplicate_key_same_hash_replays_stored();
[Fact] public Task Begin_duplicate_key_different_hash_mismatches();
[Fact] public Task Begin_while_unresolved_reports_in_flight();
[Fact] public Task One_live_authorize_index_blocks_second_attempt();
[Fact] public Task Ledger_role_cannot_update_or_delete();       // U3 append-only
[Fact] public Task Resolved_at_set_in_same_transaction_as_final_state();
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Integration.Tests`
Expected: FAIL to compile.

- [ ] **Step 3: Implement** using the [runner sketch](../../deadlock-handling.md#handling-a-deadlock), the lock order from [Prevention](../../deadlock-handling.md#prevention), and the pipelining choice from V6. The store holds no business rules: load snapshot, call `Decide`, run the conditional update, insert. Each statement list is at most about 5 round trips.
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OpenPayments.Integration.Tests`
Expected: PASS.

- [ ] **Step 5: Commit** with message `feat: add transaction runner and payment store`.

### Task 14: Create and attempt flow

**Files:**
- Create: `src/OpenPayments.Psp/IPspAdapter.cs`, `MockPspAdapter.cs`, `src/OpenPayments.Api/Program.cs`, `Auth.cs`, `PaymentFlow.cs`, `Endpoints/Payments.cs`, `CanonicalLog.cs`, `Metrics.cs`, `Json.cs`
- Test: `tests/OpenPayments.Acceptance.Tests/CreateTests.cs`, `IdempotencyTests.cs`, `TenantTests.cs`

**Interfaces:**
- Consumes: `IPspAdapter` exactly as in [PSP integration](../../architecture.md#psp-integration), the store from Task 13, the mock from Task 8, metric names from Task 10.
- Produces: `PaymentFlow.RunAttemptAsync(BeginAttempt, CancellationToken requestToken) -> ApiAnswer`, and the routes for create, attempts, status and cancel.

- [ ] **Step 1: Write failing acceptance tests**

```csharp
[Fact] public Task Approved_returns_success_ledger_and_outbox();
[Fact] public Task Declined_returns_to_awaiting_method_then_new_key_succeeds(); // one payment
[Fact] public Task E21_same_key_after_decline_replays_with_no_psp_call();
[Fact] public Task C5_same_key_100_concurrent_one_payment_one_effect_identical_response();
[Fact] public Task E1_same_key_different_body_422();
[Fact] public Task E2_same_key_in_flight_409_retry_after();
[Fact] public Task E11_invalid_amounts_rejected(string amountJson);
[Fact] public Task E12_jpy_and_kwd_stored_in_minor_units();
[Fact] public Task E3_client_disconnect_after_A_still_completes_and_retry_returns_result();
[Fact] public Task Tx_B_failure_after_approval_returns_202_stays_pending_never_decline();
[Fact] public Task Retry_after_B_failure_calls_psp_with_same_attempt_key_only();
[Fact] public Task E17_key_for_merchant_A_gets_404_on_merchant_B_payment();
[Fact] public Task Over_inflight_cap_rejects_immediately_with_retry_after();
[Fact] public Task Canonical_log_has_required_fields_and_no_card_pattern();
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/OpenPayments.Acceptance.Tests`
Expected: FAIL, since no API exists.

- [ ] **Step 3: Implement.** After transaction A commits, the flow stops using the request's token, and uses a deadline token instead, so a disconnect can't strand a charged attempt. PSP timeout maps to `UNKNOWN` and 202. Add the `Program.cs` limits from [Load control](../../engineering-standards.md#load-control). Platform overhead is total time minus PSP wait. `op_payments_completed_total` increments only after the final commit. Authentication is a SHA-256 lookup in `api_keys` with a bounded cache. Test-mode routing waits for Stage 5, so non-`live` keys get 403 for now.
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test tests/OpenPayments.Acceptance.Tests`
Expected: PASS.

- [ ] **Step 5: Commit** with message `feat: add create and attempt flow`.

### Task 15: Capture and auto-capture

**Files:**
- Modify: `src/OpenPayments.Api/PaymentFlow.cs`, `Endpoints/Payments.cs`
- Test: `tests/OpenPayments.Acceptance.Tests/CaptureTests.cs`

**Interfaces:**
- Produces: `PaymentFlow.RunCaptureAsync(CaptureRequest, CancellationToken) -> ApiAnswer`, also called by the authorize path under `capture_mode = auto`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task Capture_moves_to_captured_with_receivable_and_payable_entries();
[Fact] public Task Capture_uses_a_different_psp_key_than_authorize();          // D5
[Fact] public Task C36_two_captures_different_keys_one_psp_effect();
[Fact] public Task Declined_capture_stays_authorized_and_allows_new_capture();
[Fact] public Task Capture_timeout_202_stays_authorized_attempt_unknown();
[Fact] public Task Auto_capture_success_counts_one_completed_payment();
[Fact] public Task Auto_capture_failure_leaves_authorized_and_reports_it();
[Fact] public Task E8_capture_from_non_authorized_rejected();
```

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter CaptureTests`
Expected: FAIL.

- [ ] **Step 3: Implement.** Capture in the same request uses its own attempt row and key.
- [ ] **Step 4: Run to verify they pass**

Run: `dotnet test --filter CaptureTests`
Expected: PASS.

- [ ] **Step 5: Commit** with message `feat: add capture and auto-capture`.

### Task 16: Refunds and the Phase 1 exit gate

**Files:**
- Modify: `src/OpenPayments.Api/PaymentFlow.cs`, `src/OpenPayments.Transitions/PaymentStore.cs`
- Create: `src/OpenPayments.Api/Endpoints/Refunds.cs`, `tests/OpenPayments.Acceptance.Tests/RefundTests.cs`, `tests/OpenPayments.Acceptance.Tests/C31aEdgeCaseTests.cs`, `tests/OpenPayments.Integration.Tests/HotQueryPlanTests.cs`, `docs/phase-gates/phase-1.md`

**Interfaces:**
- Consumes: [Refund rules](../../payment-lifecycle.md#refund-rules) SQL.
- Produces: `PaymentFlow.RunRefundAsync(RefundRequest, CancellationToken) -> ApiAnswer`.

- [ ] **Step 1: Write failing tests**

```csharp
[Fact] public Task C6_50_concurrent_refunds_over_captured_never_exceed_and_decline_releases();
[Fact] public Task Worked_example_30_then_70_ends_refunded_all_clearing_consistent();
[Fact] public Task E6_refund_of_uncaptured_or_unknown_rejected();
[Fact] public Task E7_refund_over_remaining_rejected();
[Fact] public Task E13_capture_and_refund_same_moment_serialize();
[Fact] public Task Refund_timeout_stays_reserved_as_unknown();
[Fact] public Task Refund_low_balance_rejected_under_advisory_lock();
[Fact] public Task Create_path_never_takes_advisory_lock();                    // pg_locks sampled
[Fact] public Task S3_hot_queries_use_expected_indexes();                      // EXPLAIN on a seeded table
[Fact] public Task S3_each_endpoint_within_query_count();
```

Add `C31a` as one test per edge case in E1-E9, E11-E13, E16-E18 and E21 as defined in Task 1, reusing earlier tests by name where one exists.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test --filter "RefundTests|C31a|HotQueryPlanTests"`
Expected: FAIL.

- [ ] **Step 3: Implement** in lock order: advisory lock, `payments`, `refunds`, ledger, outbox.
- [ ] **Step 4: Run the whole Phase 1 gate** with run IDs recorded.

Run: `dotnet test && dotnet list package --vulnerable && dotnet build -warnaserror`
Expected: all pass. Record the commit and results for U1-U4, C5, C6, C31a, S1, S3, S5, S6 in `docs/phase-gates/phase-1.md`. S5 is the review checklist plus analyzers. List by name anything not achieved.

- [ ] **Step 5: Commit** with message `feat: add refunds and record Phase 1 gate`.

### Task 17: First ramp steps

Preview runs only ([why](#problems-found-in-the-docs)). The laptop gives comparative numbers, and steps 4-5 on Linux give the first real `C`.

**Files:**
- Create: `benchmarks/runs/<run-id>/*`, `benchmarks/BASELINE.md`
- Modify: `docs/scaling-playbook.md` (link to results only, no pasted numbers)

- [ ] **Step 1: Start one API instance, one database with standbys and the mock** at fixed 200 ms latency, with 2-5% declines and zero timeouts (no reconciler yet).
- [ ] **Step 2: Run steps 0, 1, 2 and 3, stopping at the first failure**

Run: `for s in 0 1 2 3; do benchmarks/run.sh $s --env local --stage phase1 --psp-latency 200 || break; done`
Expected: each `result.md` is valid with gates met and is labelled preview. If a gate fails, stop and use superpowers:systematic-debugging with the [bottleneck runbook](../../scaling-playbook.md#bottleneck-runbook). Don't relax the gate.

- [ ] **Step 3: On Linux (decision B), run steps 4 and 5.** Record `C`, then compute `N = ceil(T / (C × U))` with `T` of 8,300, and note the 16,700 case. Compare measured commits per payment with the Task 6 estimate.
- [ ] **Step 4: Write `BASELINE.md`:** per-request latency breakdown, the first bottleneck at each step, `C`, and whether the 5,000 target still looks reachable on one cell. State plainly anything that doesn't support the goal.
- [ ] **Step 5: Commit** the results with message `test: record first ramp baseline`.

---

## Roadmap after Task 17

Write a detailed plan with superpowers:writing-plans at the start of each stage, and read the latest measurements first. Stop and reassess if a gate says the design doesn't hold.

| Stage | Build | Benchmark run | Exit | Needs |
|---|---|---|---|---|
| **2: PSP and recovery** | Real adapter, resilience, callback receiver, reconciler and timers, settlement ingestion, one 3D Secure flow; freeze the API contract after C39 | Repeat steps 0-5 with timeouts and failures. **G2** against the sandbox | Phase 2 list in [Verification](../../verification.md#build-phases), plus C31b | Decision C |
| **3: Async** | Outbox publisher with one publisher per payment and gap parking, Redpanda, ledger projector, webhook dispatcher, inbox dedupe | Steps 6-8. First non-preview runs: **CK4 and CK6 full**. Outbox age and consumer lag | Phase 3 list, with the ordering race in C17, plus C31c | Stage 2 |
| **4: Hardening** | Kubernetes, HA with Patroni or managed, limits by request class, reserved capacity, dark-launched limiters, minimal synchronous fraud, vault and edge gateway; cells only if D26 requires them | Steps 9-11, **L1, L5, L6**, then L2, L3, L4; **G1, G3, G4, G5** closed on the full system; S2 allocation budgets | Phase 4 list | Rented cluster |
| **5: Security and operations** | Full fraud rules and decision records, test mode isolation, audit log, RBAC, runbooks and drills | **L5 again as the sign-off number**; C42 | Phase 5 list | Isolation decision, D20 |
| **Sign-off** | [Final report](../../scaling-playbook.md#final-report) | All of the above, with CK1-CK10 | All 108 and 25 rows ticked, each with a recorded run | Real PSP results |

**Benchmark checkpoints in order:**

| Checkpoint | Question | Where it can be answered |
|---|---|---|
| Spike (Task 6) | Can one primary carry the real commit rate? | Linux only |
| Baseline (Task 17) | Single-request cost; capacity per instance | Laptop for comparison, Linux for `C` |
| Steps 6-8 | Does the system scale out, and do async workers keep up? | Linux |
| Steps 9-10 and L5 | Is 5,000 completed payments/s sustained for 60 min? | Linux, generators apart from the system |
| L6, L3 | What fails first, and is overload shed cleanly? | Linux |
| L2 | Is 24 h flat? | Linux |
| L5 after Stage 5 | Does the number hold on the complete payment path? | Linux |

**Out of scope, per the docs:** payouts, disputes, marketplace, billing, machine-learning fraud, multi-region, the formal PCI assessment, and legal and banking work. A pass against the mock PSP doesn't prove production capacity: G2 and the real PSP contract may set a lower limit.
