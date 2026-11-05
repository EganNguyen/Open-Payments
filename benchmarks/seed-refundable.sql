-- Builds the pool of payments the refund share may target.
-- Run it only after the prefill exists. It does not write payment state or ledger rows.
-- Usage: psql "$DB_URL" -v ON_ERROR_STOP=1 -v count=1000 -f benchmarks/seed-refundable.sql
create table if not exists benchmark_refundable (
  payment_id   uuid primary key,
  merchant_id  uuid not null,
  amount_minor bigint not null,
  currency     char(3) not null);

truncate benchmark_refundable;

insert into benchmark_refundable (payment_id, merchant_id, amount_minor, currency)
select id,
       merchant_id,
       captured_minor - refunded_minor - refund_pending_minor,
       currency
from payments
where state in ('CAPTURED', 'PARTIALLY_REFUNDED')
  and captured_minor > refunded_minor + refund_pending_minor
order by created_at desc
limit :count;
