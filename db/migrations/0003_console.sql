-- Task C4: the staff console's own schema and roles (D40-D45). The console reads money tables
-- and writes only here; money moves through the payment API. refunds_payment shipped in 0002.

create schema console;

create table console.staff (
  id           uuid primary key,
  oidc_subject text unique not null,
  email        text not null,
  role         text not null check (role in ('viewer','operator','approver','admin')),
  disabled_at  timestamptz);

-- Append-only: no role is granted update or delete.
create table console.audit_log (
  id              bigint generated always as identity primary key,
  staff_id        uuid not null,
  phase           text not null check (phase in ('intent','outcome','event')),
  action          text not null,
  payment_id      uuid,
  refund_id       uuid,
  amount_minor    bigint,
  currency        char(3),
  reason          text,
  idempotency_key text,
  result          text,
  created_at      timestamptz not null default now());
create index audit_log_created on console.audit_log (created_at, id);
create index audit_log_key on console.audit_log (idempotency_key) where idempotency_key is not null;

-- Used from Stage 2 (Tasks C5 and C12); created now so those tasks add no schema.
create table console.review_resolutions (
  id          uuid primary key,
  payment_id  uuid not null,
  attempt_id  uuid,
  refund_id   uuid,
  kind        text not null check (kind in ('payment','refund','late_reversal')),
  outcome     text not null check (outcome in ('authorize','awaiting_method','capture','confirm','release','reversed_at_psp')),
  psp_answer  text not null,
  evidence    text not null,
  state       text not null check (state in ('proposed','rejected','applied')),
  proposed_by uuid not null,
  approved_by uuid,
  created_at  timestamptz not null default now(),
  decided_at  timestamptz,
  check (approved_by is null or approved_by <> proposed_by));
create unique index review_resolutions_one_open
  on console.review_resolutions (payment_id, coalesce(refund_id, attempt_id, payment_id))
  where state = 'proposed';

-- The REVIEW queues. Queries must use the literal state = 'REVIEW': a generic plan with a
-- parameter can't use a partial index.
create index payments_review on payments (updated_at) where state = 'REVIEW';
create index payment_attempts_review on payment_attempts (created_at) where state = 'REVIEW';
create index refunds_review on refunds (created_at) where state = 'REVIEW';

-- Group roles with no login. A deployment grants them to its own login users, so no password
-- lives in a migration. Roles are cluster-wide, so they may already exist.
do $$
begin
  if not exists (select from pg_roles where rolname = 'op_console_read') then
    create role op_console_read nologin;
  end if;
  if not exists (select from pg_roles where rolname = 'op_console') then
    create role op_console nologin;
  end if;
end $$;

grant usage on schema public to op_console_read;
grant select on payments, payment_attempts, refunds, ledger_transactions, ledger_entries, accounts, currencies
  to op_console_read;
grant usage on schema console to op_console_read, op_console, op_app;
grant select on console.staff, console.audit_log, console.review_resolutions to op_console_read;

grant select, insert on console.audit_log, console.review_resolutions to op_console;
grant update (state, approved_by, decided_at) on console.review_resolutions to op_console;
grant select, insert, update on console.staff to op_console;

grant select, update on console.review_resolutions to op_app;
grant insert on console.audit_log to op_app;
