select p.id::text as sample_id,
       abs(p.captured_minor - coalesce(l.total, 0))::bigint as impact_minor
from payments p
left join (
  select t.payment_id,
         sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end)::bigint as total
  from ledger_transactions t
  join ledger_entries e on e.txn_id = t.id
  join accounts a on a.id = e.account_id
  where t.kind = 'capture' and a.account_type = 'psp_receivable'
  group by t.payment_id
) l on l.payment_id = p.id
where p.captured_minor <> coalesce(l.total, 0);
