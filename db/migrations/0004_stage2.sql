-- Task 19: Stage 2 schema. The PSP event log, the key links and recovery columns, and the
-- per-partition autovacuum settings key expiry relies on (D19, D59-D63).
-- Expand-only: columns are nullable or have constants, so no table is rewritten.

set lock_timeout = '2s';

-- psp_events is recreated, not migrated: it has never held rows. Hash-partitioned 16 ways by
-- psp_event_id, and the primary key keeps the partition key (D59).
drop table psp_events;
create table psp_events (
  psp_account_id uuid not null,
  psp_event_id   text not null,
  source         text not null check (source in ('callback','api_answer')),
  payload        jsonb not null,
  payment_id     uuid,
  attempt_id     uuid,
  refund_id      uuid,
  received_at    timestamptz not null default now(),
  processed_at   timestamptz,
  outcome        text check (outcome in ('applied','ignored','contradiction','parked','expired')),
  attempts       int not null default 0,
  next_attempt_at timestamptz,
  last_error     text,
  primary key (psp_account_id, psp_event_id)) partition by hash (psp_event_id);
select format('create table psp_events_%s partition of psp_events for values with (modulus 16, remainder %s)', i, i)
  from generate_series(0, 15) i \gexec
create index psp_events_parked on psp_events (next_attempt_at) where processed_at is null;
create index psp_events_payment on psp_events (payment_id) where payment_id is not null;
create index psp_events_expirable on psp_events (processed_at) where processed_at is not null;

grant select, insert, update, delete on psp_events to op_app;
grant select on psp_events to op_console_read;

-- Which key started an operation, and whether finishing it resolves that key (D61).
alter table payment_attempts
  add column idempotency_key text,
  add column resolves_key boolean not null default true,
  add column next_recovery_at timestamptz,
  add column recovery_attempts int not null default 0,
  add column last_inquiry_at timestamptz,
  add column last_inquiry_result text;
alter table refunds
  add column idempotency_key text,
  add column resolves_key boolean not null default true,
  add column next_recovery_at timestamptz,
  add column recovery_attempts int not null default 0,
  add column last_inquiry_at timestamptz,
  add column last_inquiry_result text;

-- Per-account callback secret and recovery quota (D34, D59).
alter table psp_accounts
  add column callback_secret_ref text,
  add column recovery_share numeric(3,2) not null default 0.10;
alter table psp_accounts
  add constraint psp_accounts_recovery_share_range check (recovery_share > 0 and recovery_share < 1);

-- Deleting in resolved_at order collects dead index entries at the oldest end, where the
-- default 20% scale factor would wait too long at target (gate record, key expiry).
do $$
declare p text;
begin
  for p in
    select child.relname
    from pg_inherits i join pg_class child on child.oid = i.inhrelid
    where i.inhparent = 'public.idempotency_keys'::regclass
  loop
    execute format('alter table %I set (autovacuum_vacuum_scale_factor = 0.01, autovacuum_vacuum_insert_scale_factor = 0.01)', p);
  end loop;
  for p in
    select child.relname
    from pg_inherits i join pg_class child on child.oid = i.inhrelid
    where i.inhparent = 'public.psp_events'::regclass
  loop
    execute format('alter table %I set (autovacuum_vacuum_scale_factor = 0.01, autovacuum_vacuum_insert_scale_factor = 0.01)', p);
  end loop;
end $$;
