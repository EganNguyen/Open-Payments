select p.id::text as sample_id, p.amount_minor::bigint as impact_minor
from payments p
where p.state in ('CAPTURED', 'PARTIALLY_REFUNDED', 'REFUNDED', 'SETTLED')
  and not exists (
    select 1 from ledger_transactions t
    where t.payment_id = p.id and t.kind = 'capture')
union all
select t.id::text, 0::bigint
from ledger_transactions t
left join payments p on p.id = t.payment_id
where t.kind = 'capture'
  and (p.id is null or p.state not in ('CAPTURED', 'PARTIALLY_REFUNDED', 'REFUNDED', 'SETTLED'));
