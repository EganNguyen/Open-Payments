-- Fails loudly if the spike's data breaks a money rule, so a fast run with wrong money
-- can't pass. Refuses to pass on no data. Run with ON_ERROR_STOP.
do $$
declare
  bad bigint;
begin
  if not exists (select 1 from payments limit 1) then
    raise exception 'zero payments: nothing to verify';
  end if;

  select count(*) into bad from (
    select e.txn_id, e.currency
      from ledger_entries e
     group by e.txn_id, e.currency
    having sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) <> 0
  ) x;
  if bad > 0 then
    raise exception 'unbalanced ledger transactions: %', bad;
  end if;

  select count(*) into bad from payments
   where refunded_minor + refund_pending_minor > captured_minor;
  if bad > 0 then
    raise exception 'refunded plus pending above captured: %', bad;
  end if;

  select count(*) into bad from (
    select p.id
      from payments p
      left join refunds r on r.payment_id = p.id and r.state in ('PENDING','UNKNOWN')
     group by p.id, p.refund_pending_minor
    having p.refund_pending_minor <> coalesce(sum(r.amount_minor), 0)
  ) x;
  if bad > 0 then
    raise exception 'refund_pending_minor differs from unresolved refunds: %', bad;
  end if;

  select count(*) into bad from payments p
   where p.state in ('CAPTURED','PARTIALLY_REFUNDED','REFUNDED')
     and not exists (select 1 from ledger_transactions t
                      where t.payment_id = p.id and t.kind = 'capture');
  if bad > 0 then
    raise exception 'captured payments with no capture transaction: %', bad;
  end if;
end $$;
