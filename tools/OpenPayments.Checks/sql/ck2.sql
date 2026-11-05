with duplicate_effects as (
  select key,
         count(*) as copies,
         sum(amount_minor) as total,
         min(amount_minor) as one
  from ck.psp_effects
  group by key
  having count(*) > 1
)
select key as sample_id, (total - one)::bigint as impact_minor
from duplicate_effects
union all
select a.attempt_id::text, p.amount_minor::bigint
from payment_attempts a
join payments p on p.id = a.payment_id
where a.state = 'SUCCEEDED'
  and not exists (select 1 from ck.psp_effects e where e.key = a.attempt_id::text)
union all
select r.id::text, r.amount_minor::bigint
from refunds r
where r.state = 'SUCCEEDED'
  and not exists (select 1 from ck.psp_effects e where e.key = r.id::text)
union all
select e.key, e.amount_minor::bigint
from ck.psp_effects e
where not exists (select 1 from payment_attempts a where a.attempt_id::text = e.key)
  and not exists (select 1 from refunds r where r.id::text = e.key)
union all
select e.key, abs(e.amount_minor - p.amount_minor)::bigint
from ck.psp_effects e
join payment_attempts a on a.attempt_id::text = e.key
join payments p on p.id = a.payment_id
where e.amount_minor <> p.amount_minor or e.currency <> p.currency
union all
select e.key, abs(e.amount_minor - r.amount_minor)::bigint
from ck.psp_effects e
join refunds r on r.id::text = e.key
join payments p on p.id = r.payment_id
where e.amount_minor <> r.amount_minor or e.currency <> p.currency;
