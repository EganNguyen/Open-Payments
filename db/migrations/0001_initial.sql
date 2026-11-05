-- The first schema: the data model in docs/architecture.md, partitioned as D25 decides.
-- Hot tables are hash-partitioned 32 ways, so concurrent writers don't share last pages or
-- relation-extension locks. Lookups without the partition key probe every partition.

create table currencies (
  code     char(3) primary key,
  exponent smallint not null,
  rounding text not null);
insert into currencies values ('USD', 2, 'half_even'), ('JPY', 0, 'half_even'), ('KWD', 3, 'half_even');

-- D21; hot accounts use sub_account 0-63
create table accounts (
  id             uuid primary key,
  merchant_id    uuid, -- null for platform accounts
  account_type   text not null,
  sub_account    smallint not null default 0 check (sub_account between 0 and 63),
  class          text not null check (class in ('clearing','terminal')),
  class_of_money text not null check (class_of_money in ('real','logical')),
  currency       char(3) not null references currencies(code),
  unique nulls not distinct (merchant_id, account_type, sub_account, currency));

-- The only merchant record until the merchant platform is designed
create table api_keys (
  key_hash    bytea primary key, -- SHA-256; the key itself is never stored
  merchant_id uuid not null,
  mode        text not null check (mode in ('live','test')),
  scopes      text[] not null,
  revoked_at  timestamptz);

-- One merchant's connection to a PSP (D35); credentials stay in external secrets
create table psp_accounts (
  id               uuid primary key,
  merchant_id      uuid not null,
  psp              text not null,
  mode             text not null check (mode in ('live','test')),
  credential_ref   text not null,
  rate_limit_per_s int not null,
  disabled_at      timestamptz);

create table payments (
  id                   uuid not null,
  merchant_id          uuid not null,
  state                text not null,
  amount_minor         bigint not null check (amount_minor > 0),
  currency             char(3) not null,
  captured_minor       bigint not null default 0,
  refunded_minor       bigint not null default 0,
  refund_pending_minor bigint not null default 0,
  attempt_count        int not null default 0,
  event_seq            int not null default 0, -- the sequence of this payment's last event (D32)
  expires_at           timestamptz not null,
  authorized_until     timestamptz,
  version              int not null default 0,
  created_at           timestamptz not null default now(),
  updated_at           timestamptz not null default now(),
  check (refunded_minor + refund_pending_minor <= captured_minor),
  primary key (id)) partition by hash (id);
select format('create table payments_%s partition of payments for values with (modulus 32, remainder %s)', i, i)
  from generate_series(0, 31) i \gexec
create index payments_merchant_created on payments (merchant_id, created_at desc);
create index payments_unresolved on payments (updated_at) where state in ('PSP_PENDING','UNKNOWN');
create index payments_awaiting on payments (expires_at) where state = 'AWAITING_METHOD';
create index payments_authorized on payments (authorized_until) where state = 'AUTHORIZED';

-- One row per authorize, sale, capture or void, each with its own key (D5, D29, D37).
-- attempt_id alone isn't enforced as unique. UUIDv7 makes a collision unrealistic, and a lookup
-- needs payment_id to reach one partition.
create table payment_attempts (
  attempt_id        uuid not null,
  payment_id        uuid not null references payments(id),
  merchant_id       uuid not null,
  operation         text not null check (operation in ('authorize','sale','capture','void')),
  psp               text not null,
  psp_account_id    uuid not null references psp_accounts(id),
  target_attempt_id uuid, -- set when voiding a late approval, always on the same payment
  state             text not null,
  failure_code      text,
  -- How to pay: a PSP token and typed details, never card data; null on capture and void
  method_ref        text,
  method_type       text,
  method_details    jsonb,
  action_details    jsonb,
  action_expires_at timestamptz,
  psp_reference     text,
  created_at        timestamptz not null default now(),
  check (operation in ('capture','void') or (method_ref is not null and method_type is not null)),
  primary key (payment_id, attempt_id),
  foreign key (payment_id, target_attempt_id) references payment_attempts (payment_id, attempt_id)
) partition by hash (payment_id);
select format('create table payment_attempts_%s partition of payment_attempts for values with (modulus 32, remainder %s)', i, i)
  from generate_series(0, 31) i \gexec
create index payment_attempts_unresolved on payment_attempts (created_at) where state in ('PENDING','UNKNOWN');
create index payment_attempts_action_window on payment_attempts (action_expires_at) where state = 'ACTION_REQUIRED';
create index payment_attempts_psp_reference on payment_attempts (psp, psp_reference) where psp_reference is not null;
-- One live or successful authorize per payment (D16)
create unique index payment_attempts_one_live_authorize on payment_attempts (payment_id)
  where operation in ('authorize','sale') and state in ('PENDING','ACTION_REQUIRED','UNKNOWN','SUCCEEDED');
-- One live or successful capture or void per payment, so the two can't race (D37)
create unique index payment_attempts_one_live_capture on payment_attempts (payment_id)
  where operation in ('capture','void') and target_attempt_id is null and state in ('PENDING','UNKNOWN','SUCCEEDED');
-- One live or successful void of a late-approved attempt; never blocks the payment's capture
create unique index payment_attempts_one_late_void on payment_attempts (payment_id, target_attempt_id)
  where operation = 'void' and target_attempt_id is not null and state in ('PENDING','UNKNOWN','SUCCEEDED');
-- A violation names the partition's index, so give those indexes names that state the rule,
-- such as payment_attempts_15_one_live_authorize. Otherwise the application can't tell them apart.
select format('alter index %s rename to %I', i.inhrelid::regclass,
              x.indrelid::regclass::text || '_' || substr(rule, length('payment_attempts_') + 1))
  from unnest(array['payment_attempts_one_live_authorize', 'payment_attempts_one_live_capture',
                    'payment_attempts_one_late_void']) rule
  join pg_inherits i on i.inhparent = rule::regclass
  join pg_index x on x.indexrelid = i.inhrelid \gexec

-- Hash-partitioned by merchant_id, so the unique key includes the partition key
create table idempotency_keys (
  merchant_id   uuid not null,
  key           text not null,
  request_hash  bytea not null,
  payment_id    uuid,
  attempt_id    uuid,
  refund_id     uuid,
  response_code int,
  response_body jsonb,
  created_at    timestamptz not null default now(),
  resolved_at   timestamptz, -- null while the operation is unresolved; never expired then (D19)
  primary key (merchant_id, key)) partition by hash (merchant_id);
select format('create table idempotency_keys_%s partition of idempotency_keys for values with (modulus 16, remainder %s)', i, i)
  from generate_series(0, 15) i \gexec
create index idempotency_keys_expirable on idempotency_keys (resolved_at) where resolved_at is not null;

-- Range-partitioned by time, so old partitions can be dropped once published. Everything lands
-- in the default partition until the partition job exists, which must come before Stage 3.
create table outbox (
  id             uuid not null,
  merchant_id    uuid not null,
  aggregate_id   uuid not null,
  event_type     text not null,
  sequence       bigint not null,
  schema_version int not null,
  payload        jsonb not null,
  created_at     timestamptz not null default now(),
  published_at   timestamptz,
  primary key (created_at, id)) partition by range (created_at);
create table outbox_default partition of outbox default partition by hash (id);
select format('create table outbox_default_%s partition of outbox_default for values with (modulus 32, remainder %s)', i, i)
  from generate_series(0, 31) i \gexec
-- How the publisher finds unpublished rows
create index outbox_unpublished on outbox (created_at) where published_at is null;

create table psp_events (
  psp_account_id uuid not null,
  psp_event_id   text not null,
  payload        jsonb not null,
  received_at    timestamptz not null default now(),
  processed_at   timestamptz, -- null means parked, to be retried (D30)
  primary key (psp_account_id, psp_event_id));

create table refunds (
  id             uuid primary key,
  payment_id     uuid not null references payments(id),
  merchant_id    uuid not null,
  amount_minor   bigint not null check (amount_minor > 0),
  state          text not null,
  psp            text not null,
  psp_account_id uuid not null references psp_accounts(id),
  psp_reference  text,
  created_at     timestamptz not null default now());
create index refunds_unresolved on refunds (created_at) where state in ('PENDING','UNKNOWN');
create index refunds_psp_reference on refunds (psp, psp_reference) where psp_reference is not null;

-- One row per settlement file line (D18); re-ingesting a file changes nothing
create table settlement_lines (
  psp            text not null,
  psp_account_id uuid not null references psp_accounts(id),
  file_id        text not null,
  line_no        int not null,
  kind           text not null,
  psp_reference  text,
  amount_minor   bigint not null,
  currency       char(3) not null,
  state          text not null,
  merchant_id    uuid, -- set when matched
  payment_id     uuid, -- set with attempt_id, which needs it to find the attempt
  attempt_id     uuid,
  refund_id      uuid references refunds(id),
  received_at    timestamptz not null default now(),
  primary key (psp_account_id, file_id, line_no),
  check (attempt_id is null or refund_id is null),
  constraint settlement_lines_attempt_has_payment check (attempt_id is null or payment_id is not null),
  foreign key (payment_id, attempt_id) references payment_attempts (payment_id, attempt_id));
-- One matched capture or refund line per operation, so a repeated line can't post twice
create unique index settlement_lines_one_attempt on settlement_lines (attempt_id)
  where state = 'MATCHED' and attempt_id is not null;
create unique index settlement_lines_one_refund on settlement_lines (refund_id)
  where state = 'MATCHED' and refund_id is not null;

-- The ID is the source event's ID, so the primary key allows one ledger transaction per
-- business event (D25, D32). A global unique key on source_event_id isn't possible here.
create table ledger_transactions (
  id              uuid not null,
  merchant_id     uuid not null,
  payment_id      uuid,
  kind            text not null, -- authorization, capture, refund, fee, ...
  source_event_id uuid not null,
  effective_at    timestamptz not null, -- business date; a late PSP event keeps its earlier date
  created_at      timestamptz not null default now(),
  constraint ledger_transactions_id_is_source_event check (id = source_event_id),
  primary key (id)) partition by hash (id);
select format('create table ledger_transactions_%s partition of ledger_transactions for values with (modulus 32, remainder %s)', i, i)
  from generate_series(0, 31) i \gexec
create index ledger_transactions_payment on ledger_transactions (payment_id);

-- Hash-partitioned by txn_id, so the unique key includes the partition key
create table ledger_entries (
  txn_id       uuid not null,
  entry_no     smallint not null,
  merchant_id  uuid not null,
  account_id   uuid not null,
  subject_id   uuid not null, -- usually the payment, so clearing is checkable per payment
  direction    char(1) not null check (direction in ('D','C')),
  amount_minor bigint not null check (amount_minor > 0),
  currency     char(3) not null,
  created_at   timestamptz not null default now(),
  primary key (txn_id, entry_no)) partition by hash (txn_id);
select format('create table ledger_entries_%s partition of ledger_entries for values with (modulus 16, remainder %s)', i, i)
  from generate_series(0, 15) i \gexec

-- Every ledger transaction balances per currency, checked at commit
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

-- The ledger is append-only for the application (no update or delete)
grant select, insert, update on payments, payment_attempts, refunds, outbox, psp_events, settlement_lines to op_app;
grant select, insert, update, delete on idempotency_keys to op_app;
grant select, insert on ledger_transactions, ledger_entries to op_app;
grant select on accounts, currencies, api_keys, psp_accounts, schema_migrations to op_app;
