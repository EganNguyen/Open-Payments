-- Chooses payments that refund workloads target, then refreshes planner statistics.
-- Needs -v refundable=N.
begin;
set local synchronous_commit = local;
insert into spike_refundable (n, payment_id, merchant_id)
select row_number() over (), id, merchant_id
  from (select id, merchant_id from payments where state = 'CAPTURED' limit :refundable) p;
commit;
analyze;
