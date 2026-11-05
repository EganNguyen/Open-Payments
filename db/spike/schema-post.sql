-- Shared tables, triggers and grants that come after the partitioned ones.

create table psp_events (
  psp_account_id uuid not null, psp_event_id text not null, payload jsonb not null,
  received_at timestamptz not null default now(), processed_at timestamptz,
  primary key (psp_account_id, psp_event_id));

-- Not in the design yet: the publisher needs a way to find unpublished rows.
create index outbox_unpublished on outbox (created_at) where published_at is null;

-- The publisher's position, for variant V9's progress-marker option.
create table spike_outbox_position (only_row boolean primary key default true, position timestamptz not null);
insert into spike_outbox_position (position) values ('-infinity');

create table spike_refundable (n int primary key, payment_id uuid not null, merchant_id uuid not null);

-- Row-level balance check, the design's starting point. Fires once per entry at commit.
create function check_txn_balance() returns trigger language plpgsql as $$
begin
  if exists (
    select 1 from ledger_entries e where e.txn_id = new.txn_id group by e.currency
    having sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) <> 0
  ) then
    raise exception 'unbalanced ledger transaction %', new.txn_id;
  end if;
  return null;
end $$;

create constraint trigger ledger_balance after insert on ledger_entries
  deferrable initially deferred for each row execute function check_txn_balance();

grant select, insert, update on
  payments, payment_attempts, idempotency_keys, refunds, outbox, psp_events, spike_outbox_position
  to op_app;
grant select, insert, delete on idempotency_keys to op_app;
grant select, insert on ledger_transactions, ledger_entries to op_app;
grant select on accounts, currencies, api_keys, psp_accounts, spike_refundable to op_app;
