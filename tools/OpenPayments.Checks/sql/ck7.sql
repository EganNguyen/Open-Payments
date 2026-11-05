select a.id::text as sample_id,
       abs(sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end))::bigint as impact_minor
from accounts a
join ledger_entries e on e.account_id = a.id
where a.class = 'clearing'
group by a.id
having sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) <> 0
   and min(e.created_at) < now() - interval '7 days'
union all
select p.id::text,
       abs(sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end))::bigint
from payments p
join ledger_entries e on e.subject_id = p.id
join accounts a on a.id = e.account_id
where p.state in ('CAPTURED', 'PARTIALLY_REFUNDED', 'REFUNDED', 'VOIDED', 'EXPIRED', 'CANCELED')
  and a.account_type = 'auth_hold'
group by p.id
having sum(case e.direction when 'D' then e.amount_minor else -e.amount_minor end) <> 0;
