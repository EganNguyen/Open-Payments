-- Schema B: time-ordered partitions, so old data can be detached and archived.
-- Differences from schema A, each forced by a unique key needing the partition key:
--   payments and ledger partition by range on UUIDv7 IDs.
--   payment_attempts partitions by hash of payment_id, so attempt_id alone isn't unique.
--   source_event_id can't be globally unique, so a ledger transaction's ID must itself
--   be derived from its source event (D32) for the primary key to catch duplicates.
\ir schema-pre.sql

create table payments (
  id uuid not null, merchant_id uuid not null, state text not null,
  amount_minor bigint not null check (amount_minor > 0), currency char(3) not null,
  captured_minor bigint not null default 0, refunded_minor bigint not null default 0,
  refund_pending_minor bigint not null default 0, attempt_count int not null default 0,
  event_seq int not null default 0, expires_at timestamptz not null, authorized_until timestamptz,
  version int not null default 0, created_at timestamptz not null default now(),
  updated_at timestamptz not null default now(),
  check (refunded_minor + refund_pending_minor <= captured_minor),
  primary key (id)) partition by range (id);
create index payments_merchant_created on payments (merchant_id, created_at desc);
create index payments_unresolved on payments (updated_at) where state in ('PSP_PENDING','UNKNOWN');
create index payments_awaiting on payments (expires_at) where state = 'AWAITING_METHOD';
create index payments_authorized on payments (authorized_until) where state = 'AUTHORIZED';

create table payment_attempts (
  attempt_id uuid not null, payment_id uuid not null references payments(id),
  merchant_id uuid not null,
  operation text not null check (operation in ('authorize','sale','capture','void')),
  psp text not null, psp_account_id uuid not null references psp_accounts(id),
  target_attempt_id uuid,
  state text not null, failure_code text, method_ref text, method_type text,
  method_details jsonb, action_details jsonb, action_expires_at timestamptz, psp_reference text,
  created_at timestamptz not null default now(),
  check (operation in ('capture','void') or (method_ref is not null and method_type is not null)),
  primary key (payment_id, attempt_id)) partition by hash (payment_id);
select format('create table payment_attempts_%s partition of payment_attempts for values with (modulus 16, remainder %s)', i, i)
  from generate_series(0, 15) i \gexec
create index payment_attempts_unresolved on payment_attempts (created_at) where state in ('PENDING','UNKNOWN');
create index payment_attempts_action_window on payment_attempts (action_expires_at) where state = 'ACTION_REQUIRED';
create index payment_attempts_psp_reference on payment_attempts (psp, psp_reference) where psp_reference is not null;
create unique index payment_attempts_one_live_authorize on payment_attempts (payment_id)
  where operation in ('authorize','sale') and state in ('PENDING','ACTION_REQUIRED','UNKNOWN','SUCCEEDED');
create unique index payment_attempts_one_live_capture on payment_attempts (payment_id)
  where operation in ('capture','void') and target_attempt_id is null and state in ('PENDING','UNKNOWN','SUCCEEDED');
create unique index payment_attempts_one_late_void on payment_attempts (payment_id, target_attempt_id)
  where operation = 'void' and target_attempt_id is not null and state in ('PENDING','UNKNOWN','SUCCEEDED');

create table idempotency_keys (
  merchant_id uuid not null, key text not null, request_hash bytea not null,
  payment_id uuid, attempt_id uuid, refund_id uuid, response_code int, response_body jsonb,
  created_at timestamptz not null default now(), resolved_at timestamptz,
  primary key (merchant_id, key)) partition by hash (merchant_id);
select format('create table idempotency_keys_%s partition of idempotency_keys for values with (modulus 16, remainder %s)', i, i)
  from generate_series(0, 15) i \gexec
create index idempotency_keys_expirable on idempotency_keys (resolved_at) where resolved_at is not null;

create table outbox (
  id uuid not null, merchant_id uuid not null, aggregate_id uuid not null, event_type text not null,
  sequence bigint not null, schema_version int not null, payload jsonb not null,
  created_at timestamptz not null default now(), published_at timestamptz,
  primary key (created_at, id)) partition by range (created_at);
create table outbox_default partition of outbox default;

create table refunds (
  id uuid primary key, payment_id uuid not null references payments(id), merchant_id uuid not null,
  amount_minor bigint not null check (amount_minor > 0), state text not null, psp text not null,
  psp_account_id uuid not null references psp_accounts(id), psp_reference text,
  created_at timestamptz not null default now());
create index refunds_unresolved on refunds (created_at) where state in ('PENDING','UNKNOWN');
create index refunds_psp_reference on refunds (psp, psp_reference) where psp_reference is not null;

create table ledger_transactions (
  id uuid not null, merchant_id uuid not null, payment_id uuid, kind text not null,
  source_event_id uuid not null, effective_at timestamptz not null,
  created_at timestamptz not null default now(),
  primary key (id)) partition by range (id);
create index ledger_transactions_payment on ledger_transactions (payment_id);

create table ledger_entries (
  txn_id uuid not null, entry_no smallint not null, merchant_id uuid not null, account_id uuid not null,
  subject_id uuid not null, direction char(1) not null check (direction in ('D','C')),
  amount_minor bigint not null check (amount_minor > 0), currency char(3) not null,
  created_at timestamptz not null default now(),
  primary key (txn_id, entry_no)) partition by range (txn_id);

-- One partition per week around now, plus a default, for the prefill's spread of ages.
select format('create table %I partition of %I for values from (%L) to (%L)',
              t || '_w' || w, t,
              spike_uuid_floor(date_trunc('week', now()) + w * interval '7 days'),
              spike_uuid_floor(date_trunc('week', now()) + (w + 1) * interval '7 days'))
  from unnest(array['payments','ledger_transactions','ledger_entries']) t,
       generate_series(-8, 4) w \gexec
create table payments_default partition of payments default;
create table ledger_transactions_default partition of ledger_transactions default;
create table ledger_entries_default partition of ledger_entries default;

\ir schema-post.sql
